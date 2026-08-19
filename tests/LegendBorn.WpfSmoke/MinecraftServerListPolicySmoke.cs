using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using LegendBorn.Services;

internal static class MinecraftServerListPolicySmoke
{
    [ModuleInitializer]
    internal static void Run()
    {
        if (!string.Equals(
                MinecraftServerListPolicy.ResolveLaunchAddress("retired.example.invalid"),
                MinecraftServerListPolicy.CanonicalServerAddress,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Quick Play endpoint is no longer pinned to the canonical LegendBorn host.");
        }

        var temp = Path.Combine(
            Path.GetTempPath(),
            "legendborn-server-list-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            File.WriteAllBytes(Path.Combine(temp, "servers.dat"), Encoding.UTF8.GetBytes("stale server list"));
            File.WriteAllText(Path.Combine(temp, "servers.dat_old"), "old");
            File.WriteAllText(Path.Combine(temp, "servers.dat.bak"), "old");
            File.WriteAllText(Path.Combine(temp, "servers.dat.tmp"), "old");

            MinecraftServerListPolicy.EnsureCanonicalServerList(temp);

            var path = Path.Combine(temp, "servers.dat");
            var actual = File.ReadAllBytes(path);
            var expected = MinecraftServerListPolicy.GetCanonicalServersDatBytes();

            if (!actual.AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("servers.dat does not match the canonical launcher-owned payload.");

            var parsed = ParseServersDat(actual);
            if (parsed.Count != 1)
                throw new InvalidOperationException($"servers.dat contains {parsed.Count} servers; expected exactly one.");

            if (!parsed.TryGetValue("name", out var name) ||
                !string.Equals(name, MinecraftServerListPolicy.CanonicalServerName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("servers.dat canonical server name is missing or incorrect.");
            }

            if (!parsed.TryGetValue("ip", out var address) ||
                !string.Equals(address, MinecraftServerListPolicy.CanonicalServerAddress, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("servers.dat canonical LegendBorn address is missing or incorrect.");
            }

            foreach (var backup in new[] { "servers.dat_old", "servers.dat.bak", "servers.dat.tmp" })
            {
                if (File.Exists(Path.Combine(temp, backup)))
                    throw new InvalidOperationException($"Legacy multiplayer-list artifact survived: {backup}");
            }

            // Idempotency matters because the FileSystemWatcher may observe our own initial write.
            var beforeWrite = File.GetLastWriteTimeUtc(path);
            MinecraftServerListPolicy.EnsureCanonicalServerList(temp);
            var afterWrite = File.GetLastWriteTimeUtc(path);
            if (beforeWrite != afterWrite)
                throw new InvalidOperationException("Canonical servers.dat was unnecessarily rewritten on an idempotent pass.");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static Dictionary<string, string> ParseServersDat(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        RequireTag(reader, 10, "root compound");
        if (ReadNbtString(reader).Length != 0)
            throw new InvalidOperationException("servers.dat root compound must have an empty name.");

        RequireTag(reader, 9, "servers list");
        if (!string.Equals(ReadNbtString(reader), "servers", StringComparison.Ordinal))
            throw new InvalidOperationException("servers.dat root does not contain the servers list.");

        RequireTag(reader, 10, "servers list element type");
        var count = ReadInt32BigEndian(reader);
        if (count != 1)
            throw new InvalidOperationException($"servers.dat list count is {count}; expected one.");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        while (true)
        {
            var tag = reader.ReadByte();
            if (tag == 0)
                break;

            if (tag != 8)
                throw new InvalidOperationException($"Unexpected NBT tag {tag} in canonical server entry.");

            var key = ReadNbtString(reader);
            var value = ReadNbtString(reader);
            fields[key] = value;
        }

        RequireTag(reader, 0, "root compound end");
        if (stream.Position != stream.Length)
            throw new InvalidOperationException("servers.dat contains unexpected trailing bytes.");

        fields["__count"] = count.ToString();
        return new ParsedServerFields(fields, count);
    }

    private static void RequireTag(BinaryReader reader, byte expected, string context)
    {
        var actual = reader.ReadByte();
        if (actual != expected)
            throw new InvalidOperationException($"Unexpected NBT tag for {context}: {actual}, expected {expected}.");
    }

    private static string ReadNbtString(BinaryReader reader)
    {
        Span<byte> lengthBytes = stackalloc byte[2];
        reader.BaseStream.ReadExactly(lengthBytes);
        var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException("Unexpected end of NBT string.");

        return Encoding.UTF8.GetString(bytes);
    }

    private static int ReadInt32BigEndian(BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[4];
        reader.BaseStream.ReadExactly(bytes);
        return BinaryPrimitives.ReadInt32BigEndian(bytes);
    }

    private sealed class ParsedServerFields : Dictionary<string, string>
    {
        public ParsedServerFields(Dictionary<string, string> source, int count)
            : base(source, StringComparer.Ordinal)
        {
            CountValue = count;
        }

        public int CountValue { get; }
        public new int Count => CountValue;
    }
}
