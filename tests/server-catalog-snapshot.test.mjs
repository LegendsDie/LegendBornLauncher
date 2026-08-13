import assert from "node:assert/strict";
import test from "node:test";
import { serializeCatalog, sha256Text, validateCatalog } from "../scripts/server-catalog-snapshot.mjs";

const NOW = 2_000_000_000;
const TRUSTED_SHA = "68eeab77059ba53df1812f1afa5bf530ab2566a3cdcd5f924aa6e71be42e410c";

function makeCatalog() {
  return {
    version: 4,
    generatedAtUnix: NOW - 60,
    validUntilUnix: NOW + 82800,
    servers: [{
      id: "legendCraft",
      name: "LegendCraft",
      address: "legendborn.minerent.io",
      minecraftVersion: "1.21.1",
      loader: {
        type: "neoforge",
        version: "21.1.248",
        installerUrl: "https://mirror-a.invalid/maven/neoforge.jar",
        installerMirrors: ["https://mirror-a.invalid/maven/neoforge.jar", "https://mirror-b.invalid/maven/neoforge.jar"],
        installerSha256: TRUSTED_SHA,
        mavenMirrors: ["https://mirror-a.invalid/maven/", "https://mirror-b.invalid/maven/"],
        installerMirrorArgument: "--mirror"
      },
      clientVersionId: "LegendBorn",
      packBaseUrl: "https://mirror-a.invalid/launcher/pack/",
      packMirrors: ["https://mirror-a.invalid/launcher/pack/", "https://mirror-b.invalid/launcher/pack/"],
      syncPack: true
    }]
  };
}

test("accepts a fresh full NeoForge distribution contract", () => {
  const catalog = makeCatalog();
  assert.equal(validateCatalog(catalog, { nowUnix: NOW }), catalog);
});

test("rejects stale or missing freshness metadata", () => {
  const missing = makeCatalog();
  delete missing.generatedAtUnix;
  assert.throws(() => validateCatalog(missing, { nowUnix: NOW }), /generatedAtUnix/);

  const expired = makeCatalog();
  expired.validUntilUnix = NOW - 1;
  assert.throws(() => validateCatalog(expired, { nowUnix: NOW }), /expired/);

  const tooLong = makeCatalog();
  tooLong.validUntilUnix = tooLong.generatedAtUnix + 49 * 3600;
  assert.throws(() => validateCatalog(tooLong, { nowUnix: NOW }), /48 hours/);
});

test("rejects weak NeoForge trust contracts", () => {
  const hash = makeCatalog();
  hash.servers[0].loader.installerSha256 = "0".repeat(63);
  assert.throws(() => validateCatalog(hash, { nowUnix: NOW }), /64 hexadecimal/);

  const insecure = makeCatalog();
  insecure.servers[0].loader.mavenMirrors[0] = "http://mirror-a.invalid/maven/";
  assert.throws(() => validateCatalog(insecure, { nowUnix: NOW }), /HTTPS/);

  const wrongArgument = makeCatalog();
  wrongArgument.servers[0].loader.installerMirrorArgument = "--repository";
  assert.throws(() => validateCatalog(wrongArgument, { nowUnix: NOW }), /--mirror/);
});

test("requires installerUrl to belong to installerMirrors", () => {
  const catalog = makeCatalog();
  catalog.servers[0].loader.installerUrl = "https://other.invalid/neoforge.jar";
  assert.throws(() => validateCatalog(catalog, { nowUnix: NOW }), /installerUrl must be present/);
});

test("serialization and SHA-256 are deterministic", () => {
  const catalog = makeCatalog();
  const first = serializeCatalog(catalog);
  const second = serializeCatalog(catalog);
  assert.equal(first, second);
  assert.equal(sha256Text(first), sha256Text(second));
  assert.match(sha256Text(first), /^[a-f0-9]{64}$/);
});
