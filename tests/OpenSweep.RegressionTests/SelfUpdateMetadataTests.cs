using System.Buffers.Binary;
using System.Text;
using OpenSweep.Core;

internal static partial class Program
{
    static async Task SelfUpdateMetadataTests()
    {
        await Run("Embedded update metadata supports PE32 and PE32+ version resources", EmbeddedMetadataValid);
        await Run("Embedded update version uses numeric fixed metadata instead of display strings", EmbeddedMetadataNumericVersion);
        await Run("Embedded update metadata accepts consistent localized resources", EmbeddedMetadataConsistent);
        await Run("Embedded update metadata rejects conflicting localized identities", EmbeddedMetadataConflict);
        await Run("Embedded update metadata rejects malformed PE headers and resource bounds", EmbeddedMetadataBounds);
        await Run("Embedded update metadata rejects missing or malformed version values", EmbeddedMetadataMalformedVersion);
        await Run("Embedded update metadata reads only the supplied executable stream", EmbeddedMetadataStreamOnly);
    }

    // Construct ordinary PE resource data from its documented layout. The fixture contains
    // no executable code and is never launched; no binary files are committed to the repo.
    static byte[] VersionNode(string key, ushort type, byte[] value, ushort valueLength, params byte[][] children)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.Unicode, true);
        writer.Write((ushort)0); writer.Write(valueLength); writer.Write(type);
        writer.Write(Encoding.Unicode.GetBytes(key + "\0"));
        while (output.Length % 4 != 0) writer.Write((byte)0);
        writer.Write(value);
        foreach (var child in children)
        {
            while (output.Length % 4 != 0) writer.Write((byte)0);
            writer.Write(child);
        }
        var bytes = output.ToArray(); Put16(bytes, 0, checked((ushort)bytes.Length)); return bytes;
    }

    static byte[] VersionString(string key, string value) => VersionNode(key, 1, Encoding.Unicode.GetBytes(value + "\0"), checked((ushort)(value.Length + 1)));

    static byte[] VersionResource(string product = "OpenSweep", Version version = null, string localizedProduct = null, string displayVersion = "1.2.3.0")
    {
        version ??= new Version(1, 2, 3, 0);
        var fixedInfo = new byte[52];
        Put32(fixedInfo, 0, 0xFEEF04BD); Put32(fixedInfo, 4, 0x00010000);
        Put32(fixedInfo, 8, (uint)((version.Major << 16) | version.Minor));
        Put32(fixedInfo, 12, (uint)((version.Build << 16) | Math.Max(0, version.Revision)));
        Put32(fixedInfo, 24, 0x3f); Put32(fixedInfo, 32, 4); Put32(fixedInfo, 36, 1);
        var tables = new List<byte[]> { VersionNode("040904b0", 1, Array.Empty<byte>(), 0,
            VersionString("ProductName", product), VersionString("FileVersion", displayVersion)) };
        if (localizedProduct != null) tables.Add(VersionNode("041104b0", 1, Array.Empty<byte>(), 0, VersionString("ProductName", localizedProduct)));
        return VersionNode("VS_VERSION_INFO", 0, fixedInfo, 52, VersionNode("StringFileInfo", 1, Array.Empty<byte>(), 0, tables.ToArray()));
    }

    static byte[] MetadataPe(bool pe64 = false, params byte[][] versions)
    {
        if (versions.Length == 0) versions = new[] { VersionResource() };
        int dataStart = 64 + versions.Length * 8;
        int payload = dataStart + versions.Length * 16;
        int resourceLength = payload + versions.Sum(value => (value.Length + 3) & ~3);
        var bytes = new byte[0x200 + ((resourceLength + 0x1ff) & ~0x1ff)];
        Put16(bytes, 0, 0x5a4d); Put32(bytes, 0x3c, 0x80);
        Put32(bytes, 0x80, 0x00004550); Put16(bytes, 0x84, pe64 ? (ushort)0x8664 : (ushort)0x14c); Put16(bytes, 0x86, 1);
        int optionalSize = pe64 ? 0xf0 : 0xe0;
        Put16(bytes, 0x94, (ushort)optionalSize); Put16(bytes, 0x96, 0x0102);
        int optional = 0x98; Put16(bytes, optional, pe64 ? (ushort)0x20b : (ushort)0x10b);
        Put32(bytes, optional + 32, 0x1000); Put32(bytes, optional + 36, 0x200);
        Put32(bytes, optional + 56, 0x3000); Put32(bytes, optional + 60, 0x200);
        Put32(bytes, optional + (pe64 ? 108 : 92), 16);
        int resourceDirectory = optional + (pe64 ? 112 : 96) + 16;
        Put32(bytes, resourceDirectory, 0x2000); Put32(bytes, resourceDirectory + 4, (uint)resourceLength);
        int section = optional + optionalSize;
        Encoding.ASCII.GetBytes(".rsrc\0\0\0").CopyTo(bytes, section);
        Put32(bytes, section + 8, (uint)resourceLength); Put32(bytes, section + 12, 0x2000);
        Put32(bytes, section + 16, (uint)(bytes.Length - 0x200)); Put32(bytes, section + 20, 0x200); Put32(bytes, section + 36, 0x40000040);
        const int raw = 0x200;
        Put16(bytes, raw + 14, 1); Put32(bytes, raw + 16, 16); Put32(bytes, raw + 20, 0x80000018);
        Put16(bytes, raw + 24 + 14, 1); Put32(bytes, raw + 40, 1); Put32(bytes, raw + 44, 0x80000030);
        Put16(bytes, raw + 48 + 14, checked((ushort)versions.Length));
        for (int i = 0; i < versions.Length; i++)
        {
            Put32(bytes, raw + 64 + i * 8, (uint)(0x409 + i)); Put32(bytes, raw + 68 + i * 8, (uint)(dataStart + i * 16));
            Put32(bytes, raw + dataStart + i * 16, (uint)(0x2000 + payload));
            Put32(bytes, raw + dataStart + i * 16 + 4, (uint)versions[i].Length); Put32(bytes, raw + dataStart + i * 16 + 8, 1200);
            versions[i].CopyTo(bytes, raw + payload); payload += (versions[i].Length + 3) & ~3;
        }
        return bytes;
    }

    static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
    static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    static SelfUpdateMetadata ReadMetadata(byte[] bytes) { using var input = new MemoryStream(bytes, false); return SelfUpdateMetadata.Read(input); }
    static int FindBytes(byte[] bytes, byte[] value)
    {
        for (int i = 0; i <= bytes.Length - value.Length; i++) if (bytes.AsSpan(i, value.Length).SequenceEqual(value)) return i;
        throw new Exception("Required byte pattern was absent from the fixture");
    }

    static Task EmbeddedMetadataValid() => Sync(() =>
    {
        foreach (bool pe64 in new[] { false, true })
        {
            var metadata = ReadMetadata(MetadataPe(pe64));
            Equal("OpenSweep", metadata.ProductName); Equal(new Version(1, 2, 3, 0), metadata.FileVersion);
        }
    });

    static Task EmbeddedMetadataNumericVersion() => Sync(() =>
    {
        var metadata = ReadMetadata(MetadataPe(false, VersionResource(version: new Version(1, 0, 0, 0), displayVersion: "99.0.0.0")));
        Equal(new Version(1, 0, 0, 0), metadata.FileVersion);
        Check(!SelfUpdatePolicy.MetadataMatchesRelease(metadata.ProductName, metadata.FileVersion, new Version(99, 0, 0), new Version(1, 0, 0)), "A display string relabeled an old executable as a new release");
    });

    static Task EmbeddedMetadataConsistent() => Sync(() =>
    {
        var metadata = ReadMetadata(MetadataPe(false, VersionResource(localizedProduct: "OpenSweep"), VersionResource()));
        Equal("OpenSweep", metadata.ProductName); Equal(new Version(1, 2, 3, 0), metadata.FileVersion);
    });

    static Task EmbeddedMetadataConflict() => Sync(() =>
    {
        Throws<InvalidDataException>(() => ReadMetadata(MetadataPe(false, VersionResource(localizedProduct: "Unrelated product"))));
        Throws<InvalidDataException>(() => ReadMetadata(MetadataPe(false, VersionResource(), VersionResource("Unrelated product"))));
        Throws<InvalidDataException>(() => ReadMetadata(MetadataPe(false, VersionResource(), VersionResource(version: new Version(1, 2, 4, 0)))));
    });

    static Task EmbeddedMetadataBounds() => Sync(() =>
    {
        foreach (var corrupt in new Action<byte[]>[]
        {
            bytes => Put16(bytes, 0, 0), bytes => Put32(bytes, 0x80, 0), bytes => Put32(bytes, 0x3c, uint.MaxValue),
            bytes => Put16(bytes, 0x98, 0), bytes => Put16(bytes, 0x86, 0),
            bytes => Put32(bytes, 0x98 + 96 + 16, 0x6000),
            bytes => Put32(bytes, 0x178 + 20, uint.MaxValue),
            bytes => Put32(bytes, 0x200 + 20, 0x8000fff0),
            bytes => Put32(bytes, 0x200 + 72, uint.MaxValue),
            bytes => Put32(bytes, 0x200 + 76, uint.MaxValue)
        })
        { var bytes = MetadataPe(); corrupt(bytes); Throws<InvalidDataException>(() => ReadMetadata(bytes)); }
        var complete = MetadataPe();
        foreach (int length in new[] { 0, 63, 0x84, 0x98, 0x190, 0x210, 0x250 })
            Throws<InvalidDataException>(() => ReadMetadata(complete.Take(length).ToArray()));
    });

    static Task EmbeddedMetadataMalformedVersion() => Sync(() =>
    {
        var absent = MetadataPe(); Put32(absent, 0x200 + 16, 10);
        Throws<InvalidDataException>(() => ReadMetadata(absent));
        var signature = VersionResource(); Put32(signature, FindBytes(signature, new byte[] { 0xbd, 0x04, 0xef, 0xfe }), 0);
        Throws<InvalidDataException>(() => ReadMetadata(MetadataPe(false, signature)));
        var length = VersionResource(); Put16(length, 0, ushort.MaxValue);
        Throws<InvalidDataException>(() => ReadMetadata(MetadataPe(false, length)));
        var unterminated = VersionResource(); int product = FindBytes(unterminated, Encoding.Unicode.GetBytes("OpenSweep\0"));
        Put16(unterminated, product + "OpenSweep".Length * 2, 'X');
        Throws<InvalidDataException>(() => ReadMetadata(MetadataPe(false, unterminated)));
    });

    static Task EmbeddedMetadataStreamOnly() => Sync(() =>
    {
        string directory = Fixture("metadata-stream"); string path = Path.Combine(directory, "OpenSweep.exe");
        File.WriteAllBytes(path, MetadataPe(false, VersionResource("Other product", new Version(1, 0, 0, 0))));
        Directory.CreateDirectory(Path.Combine(directory, "en-US"));
        File.WriteAllBytes(Path.Combine(directory, "en-US", "OpenSweep.exe.mui"), MetadataPe(false, VersionResource("OpenSweep", new Version(9, 0, 0, 0))));
        using var input = File.OpenRead(path); var metadata = SelfUpdateMetadata.Read(input);
        Equal("Other product", metadata.ProductName); Equal(new Version(1, 0, 0, 0), metadata.FileVersion);
    });
}
