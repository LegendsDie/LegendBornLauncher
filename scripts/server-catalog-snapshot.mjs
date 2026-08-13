import { createHash } from "node:crypto";
import { mkdir, readFile, rename, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const MAX_CATALOG_BYTES = 2 * 1024 * 1024;
const FUTURE_CLOCK_SKEW_SECONDS = 5 * 60;
const MAX_EXPLICIT_LIFETIME_SECONDS = 48 * 60 * 60;
const MAX_IMPLICIT_AGE_SECONDS = 24 * 60 * 60;
const MIN_REMAINING_LIFETIME_SECONDS = 15 * 60;
const DEFAULT_SOURCE_URL = "https://legendborn.xyz/api/launcher/servers";
const SHA256_RE = /^[a-f0-9]{64}$/i;

function fail(message) {
  throw new Error(message);
}

function requireString(value, label) {
  if (typeof value !== "string" || value.trim() === "") fail(`${label} must be a non-empty string`);
  return value.trim();
}

function requirePositiveInteger(value, label) {
  if (!Number.isSafeInteger(value) || value <= 0) fail(`${label} must be a positive integer`);
  return value;
}

function requireHttpsUrl(value, label, { base = false } = {}) {
  const raw = requireString(value, label);
  let url;
  try {
    url = new URL(raw);
  } catch {
    fail(`${label} must be a valid absolute URL`);
  }
  if (url.protocol !== "https:") fail(`${label} must use HTTPS`);
  if (url.username || url.password) fail(`${label} must not contain credentials`);
  if (base && !url.pathname.endsWith("/")) fail(`${label} must end with '/'`);
  return raw;
}

function requireHttpsArray(value, label, options) {
  if (!Array.isArray(value) || value.length === 0) fail(`${label} must be a non-empty array`);
  const normalized = value.map((item, index) => requireHttpsUrl(item, `${label}[${index}]`, options));
  if (new Set(normalized.map((item) => item.toLowerCase())).size !== normalized.length) {
    fail(`${label} must not contain duplicate URLs`);
  }
  return normalized;
}

function validateLoader(loader, label) {
  if (!loader || typeof loader !== "object" || Array.isArray(loader)) fail(`${label} must be an object`);
  const type = requireString(loader.type, `${label}.type`).toLowerCase();
  if (type !== "neoforge") fail(`${label}.type must be 'neoforge'`);

  requireString(loader.version, `${label}.version`);
  const installerUrl = requireHttpsUrl(loader.installerUrl, `${label}.installerUrl`);
  const installerMirrors = requireHttpsArray(loader.installerMirrors, `${label}.installerMirrors`);
  if (!installerMirrors.some((url) => url.toLowerCase() === installerUrl.toLowerCase())) {
    fail(`${label}.installerUrl must be present in installerMirrors`);
  }

  const sha = requireString(loader.installerSha256, `${label}.installerSha256`);
  if (!SHA256_RE.test(sha)) fail(`${label}.installerSha256 must be exactly 64 hexadecimal characters`);

  requireHttpsArray(loader.mavenMirrors, `${label}.mavenMirrors`, { base: true });
  if (loader.installerMirrorArgument !== "--mirror") {
    fail(`${label}.installerMirrorArgument must be '--mirror'`);
  }
}

function validateServer(server, index) {
  const label = `servers[${index}]`;
  if (!server || typeof server !== "object" || Array.isArray(server)) fail(`${label} must be an object`);
  requireString(server.id, `${label}.id`);
  requireString(server.name, `${label}.name`);
  const address = requireString(server.address, `${label}.address`);
  if (/\s|[\\/]|:\/\//u.test(address)) fail(`${label}.address has an invalid format`);
  requireString(server.minecraftVersion, `${label}.minecraftVersion`);
  requireString(server.clientVersionId, `${label}.clientVersionId`);
  validateLoader(server.loader, `${label}.loader`);

  if (server.syncPack === true) {
    const packBaseUrl = requireHttpsUrl(server.packBaseUrl, `${label}.packBaseUrl`, { base: true });
    const packMirrors = requireHttpsArray(server.packMirrors, `${label}.packMirrors`, { base: true });
    if (!packMirrors.some((url) => url.toLowerCase() === packBaseUrl.toLowerCase())) {
      fail(`${label}.packBaseUrl must be present in packMirrors`);
    }
  }
}

export function validateCatalog(catalog, { nowUnix = Math.floor(Date.now() / 1000), requireRemainingLifetime = true } = {}) {
  if (!catalog || typeof catalog !== "object" || Array.isArray(catalog)) fail("catalog must be an object");

  requirePositiveInteger(catalog.version, "version");
  const generatedAtUnix = requirePositiveInteger(catalog.generatedAtUnix, "generatedAtUnix");
  if (generatedAtUnix > nowUnix + FUTURE_CLOCK_SKEW_SECONDS) fail("generatedAtUnix is too far in the future");

  if (catalog.validUntilUnix == null || catalog.validUntilUnix === 0) {
    if (nowUnix - generatedAtUnix > MAX_IMPLICIT_AGE_SECONDS) fail("implicit-expiry catalog is too old");
  } else {
    const validUntilUnix = requirePositiveInteger(catalog.validUntilUnix, "validUntilUnix");
    if (validUntilUnix <= generatedAtUnix) fail("validUntilUnix must be later than generatedAtUnix");
    if (validUntilUnix - generatedAtUnix > MAX_EXPLICIT_LIFETIME_SECONDS) fail("catalog lifetime exceeds 48 hours");
    if (validUntilUnix <= nowUnix) fail("catalog is expired");
    if (requireRemainingLifetime && validUntilUnix - nowUnix < MIN_REMAINING_LIFETIME_SECONDS) {
      fail("catalog has less than 15 minutes of remaining lifetime");
    }
  }

  if (!Array.isArray(catalog.servers) || catalog.servers.length === 0) fail("servers must be a non-empty array");
  const ids = new Set();
  catalog.servers.forEach((server, index) => {
    validateServer(server, index);
    const id = server.id.trim().toLowerCase();
    if (ids.has(id)) fail(`duplicate server id: ${server.id}`);
    ids.add(id);
  });

  return catalog;
}

export function serializeCatalog(catalog) {
  return `${JSON.stringify(catalog, null, 2)}\n`;
}

export function sha256Text(text) {
  return createHash("sha256").update(text, "utf8").digest("hex");
}

async function readCatalogFromFile(filePath) {
  const bytes = await readFile(filePath);
  if (bytes.length <= 0 || bytes.length > MAX_CATALOG_BYTES) fail(`catalog file size is invalid: ${bytes.length}`);
  return JSON.parse(bytes.toString("utf8"));
}

async function fetchCatalog(sourceUrl) {
  const requested = new URL(requireHttpsUrl(sourceUrl, "source URL"));
  const response = await fetch(requested, {
    method: "GET",
    redirect: "follow",
    cache: "no-store",
    headers: {
      Accept: "application/json",
      "User-Agent": "LegendBornLauncher-CatalogSnapshot/1.0",
    },
    signal: AbortSignal.timeout(15_000),
  });

  if (!response.ok) fail(`catalog source returned HTTP ${response.status}`);
  const finalUrl = new URL(response.url);
  if (finalUrl.origin !== requested.origin) fail(`catalog source redirected to another origin: ${finalUrl.origin}`);

  const mediaType = (response.headers.get("content-type") ?? "").toLowerCase();
  if (mediaType.includes("text/html")) fail("catalog source returned HTML instead of JSON");
  const declaredLength = Number(response.headers.get("content-length") ?? 0);
  if (Number.isFinite(declaredLength) && declaredLength > MAX_CATALOG_BYTES) fail("catalog source is larger than 2 MiB");

  const bytes = Buffer.from(await response.arrayBuffer());
  if (bytes.length <= 0 || bytes.length > MAX_CATALOG_BYTES) fail(`catalog response size is invalid: ${bytes.length}`);
  return JSON.parse(bytes.toString("utf8"));
}

async function writeAtomic(outputPath, text) {
  const resolved = path.resolve(outputPath);
  await mkdir(path.dirname(resolved), { recursive: true });
  const temp = `${resolved}.tmp-${process.pid}-${Date.now()}`;
  try {
    await writeFile(temp, text, { encoding: "utf8", flag: "wx" });
    await rename(temp, resolved);
  } catch (error) {
    try {
      const { unlink } = await import("node:fs/promises");
      await unlink(temp);
    } catch {}
    throw error;
  }
}

function parseArgs(argv) {
  const result = { sourceUrl: DEFAULT_SOURCE_URL, inputFile: "", outputFile: "" };
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    const value = argv[index + 1];
    if (arg === "--url" && value) {
      result.sourceUrl = value;
      index += 1;
    } else if (arg === "--file" && value) {
      result.inputFile = value;
      index += 1;
    } else if (arg === "--out" && value) {
      result.outputFile = value;
      index += 1;
    } else if (arg === "--help" || arg === "-h") {
      result.help = true;
    } else {
      fail(`unknown or incomplete argument: ${arg}`);
    }
  }
  if (result.inputFile && result.sourceUrl !== DEFAULT_SOURCE_URL) fail("use either --file or --url, not both");
  return result;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.help) {
    console.log("Usage: node scripts/server-catalog-snapshot.mjs [--url https://...] [--file catalog.json] [--out servers.json]");
    return;
  }

  const catalog = args.inputFile ? await readCatalogFromFile(args.inputFile) : await fetchCatalog(args.sourceUrl);
  validateCatalog(catalog);
  const text = serializeCatalog(catalog);
  const digest = sha256Text(text);
  if (args.outputFile) await writeAtomic(args.outputFile, text);

  console.log(`Catalog OK: revision=${catalog.version}, generatedAtUnix=${catalog.generatedAtUnix}, validUntilUnix=${catalog.validUntilUnix}, servers=${catalog.servers.length}`);
  console.log(`SHA256=${digest}`);
  if (args.outputFile) console.log(`Written=${path.resolve(args.outputFile)}`);
}

const isMain = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  main().catch((error) => {
    console.error(`ERROR: ${error?.message ?? error}`);
    process.exitCode = 1;
  });
}
