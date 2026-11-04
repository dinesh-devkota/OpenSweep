using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenSweep.Core
{
    /// <summary>
    /// Reads only VERSIONINFO bytes embedded in the verified PE stream. Windows version APIs
    /// can merge strings from unsigned .mui sidecars, including with FILE_VER_GET_NEUTRAL.
    /// </summary>
    public sealed class SelfUpdateMetadata
    {
        public string ProductName { get; }
        public Version FileVersion { get; }
        SelfUpdateMetadata(string product, Version version) { ProductName = product; FileVersion = version; }

        public static SelfUpdateMetadata Read(Stream stream)
        {
            if (stream == null || !stream.CanRead || !stream.CanSeek) throw Bad("A readable, seekable PE stream is required.");
            long previous = stream.Position;
            try
            {
                var dos = Bytes(stream, 0, 64);
                if (U16(dos, 0) != 0x5a4d) throw Bad("The file has no DOS header.");
                long peOffset = U32(dos, 60);
                if (peOffset < 64) throw Bad("The PE header offset is invalid.");
                var pe = Bytes(stream, peOffset, 24);
                if (U32(pe, 0) != 0x00004550) throw Bad("The PE signature is invalid.");
                int sectionCount = U16(pe, 6), optionalLength = U16(pe, 20);
                if (sectionCount == 0 || sectionCount > 96 || optionalLength > 4096) throw Bad("The PE header dimensions are invalid.");
                var optional = Bytes(stream, peOffset + 24, optionalLength);
                int magic = U16(optional, 0), directories;
                if (magic == 0x10b) directories = 96;
                else if (magic == 0x20b) directories = 112;
                else throw Bad("The optional PE header is unsupported.");
                if (U32(optional, directories - 4) <= 2) throw Bad("The PE has no resource directory.");
                uint resourceRva = U32(optional, directories + 16), resourceLength = U32(optional, directories + 20);
                if (resourceRva == 0 || resourceLength < 16 || resourceLength > 16 * 1024 * 1024) throw Bad("The resource directory is absent or too large.");
                var sections = Bytes(stream, peOffset + 24 + optionalLength, sectionCount * 40);
                var resource = Bytes(stream, MapRva(resourceRva, resourceLength, sections), checked((int)resourceLength));
                var versions = new List<SelfUpdateMetadata>();
                foreach (var type in DirectoryEntries(resource, 0))
                {
                    if (type.Name != 16) continue;
                    if (!type.IsDirectory) throw Bad("The version resource type is not a directory.");
                    foreach (var name in DirectoryEntries(resource, type.Offset))
                    {
                        if (!name.IsDirectory) throw Bad("The version resource name is not a directory.");
                        foreach (var language in DirectoryEntries(resource, name.Offset))
                        {
                            if (language.IsDirectory) throw Bad("The version resource tree is too deep.");
                            uint dataRva = U32(resource, language.Offset), size = U32(resource, language.Offset + 4);
                            if (size == 0 || size > 1024 * 1024 || versions.Count >= 64) throw Bad("The embedded version resource exceeds its limits.");
                            var data = Bytes(stream, MapRva(dataRva, size, sections), checked((int)size));
                            versions.Add(ReadVersion(data));
                        }
                    }
                }
                if (versions.Count == 0) throw Bad("The PE has no embedded version metadata.");
                var result = versions[0];
                foreach (var version in versions)
                    if (version.ProductName != result.ProductName || version.FileVersion != result.FileVersion)
                        throw Bad("Embedded version resources disagree about the product or version.");
                return result;
            }
            finally { stream.Position = previous; }
        }

        static long MapRva(uint rva, uint size, byte[] sections)
        {
            long found = -1;
            for (int i = 0; i < sections.Length; i += 40)
            {
                uint address = U32(sections, i + 12), rawSize = U32(sections, i + 16), rawOffset = U32(sections, i + 20);
                if (rva < address) continue;
                long delta = (long)rva - address;
                if (delta > rawSize || size > rawSize - delta) continue;
                if (found >= 0) throw Bad("Overlapping PE sections make the resource location ambiguous.");
                found = rawOffset + delta;
            }
            if (found < 0) throw Bad("A resource points outside the PE's stored sections.");
            return found;
        }

        sealed class ResourceEntry
        {
            public uint Name;
            public int Offset;
            public bool IsDirectory;
        }
        static IEnumerable<ResourceEntry> DirectoryEntries(byte[] data, int offset)
        {
            Need(data, offset, 16);
            int count = U16(data, offset + 12) + U16(data, offset + 14);
            if (count > 1024) throw Bad("The resource directory has too many entries.");
            Need(data, offset + 16, count * 8);
            for (int i = 0; i < count; i++)
            {
                int entry = offset + 16 + i * 8;
                uint target = U32(data, entry + 4);
                int next = (int)(target & 0x7fffffff);
                Need(data, next, 16);
                yield return new ResourceEntry { Name = U32(data, entry), Offset = next, IsDirectory = (target & 0x80000000) != 0 };
            }
        }

        sealed class VersionNode
        {
            public string Key;
            public int End, ValueOffset, ValueBytes, ChildrenOffset;
            public ushort Type;
        }
        static SelfUpdateMetadata ReadVersion(byte[] data)
        {
            var root = Node(data, 0, data.Length);
            if (root.Key != "VS_VERSION_INFO" || root.Type != 0 || root.ValueBytes != 52 || U32(data, root.ValueOffset) != 0xfeef04bd)
                throw Bad("The embedded fixed-version information is invalid.");
            uint ms = U32(data, root.ValueOffset + 8), ls = U32(data, root.ValueOffset + 12);
            var version = new Version((int)(ms >> 16), (int)(ms & 0xffff), (int)(ls >> 16), (int)(ls & 0xffff));
            string product = null;
            foreach (var group in Children(data, root))
            {
                if (group.Key != "StringFileInfo") continue;
                foreach (var table in Children(data, group))
                {
                    foreach (var text in Children(data, table))
                    {
                        if (text.Key != "ProductName") continue;
                        if (text.Type != 1 || text.ValueBytes == 0 || text.ValueBytes > 1024) throw Bad("The embedded product name is invalid.");
                        if (U16(data, text.ValueOffset + text.ValueBytes - 2) != 0) throw Bad("The embedded product name is not terminated.");
                        string name = Utf16(data, text.ValueOffset, text.ValueBytes).TrimEnd('\0');
                        if (name.Length == 0 || name.IndexOf('\0') >= 0) throw Bad("The embedded product name is invalid.");
                        if (product != null && name != product) throw Bad("Embedded product names disagree across languages.");
                        product = name;
                    }
                }
            }
            if (product == null) throw Bad("The signed PE has no embedded product name.");
            return new SelfUpdateMetadata(product, version);
        }

        static VersionNode Node(byte[] data, int offset, int parentEnd)
        {
            Need(data, offset, 6);
            int length = U16(data, offset), valueLength = U16(data, offset + 2);
            ushort type = U16(data, offset + 4);
            if (length < 8 || offset > parentEnd - length || type > 1) throw Bad("A version-information block is invalid.");
            int end = offset + length, keyStart = offset + 6, cursor = keyStart;
            while (cursor <= end - 2 && U16(data, cursor) != 0)
            {
                cursor += 2;
                if (cursor - keyStart > 1024) throw Bad("A version-information key is too long.");
            }
            if (cursor > end - 2) throw Bad("A version-information key is not terminated.");
            string key = Utf16(data, keyStart, cursor - keyStart);
            int value = Align4(cursor + 2), valueBytes = type == 1 ? valueLength * 2 : valueLength;
            if (value > end || valueBytes > end - value) throw Bad("A version-information value extends outside its block.");
            return new VersionNode { Key = key, End = end, Type = type, ValueOffset = value, ValueBytes = valueBytes, ChildrenOffset = Align4(value + valueBytes) };
        }
        static IEnumerable<VersionNode> Children(byte[] data, VersionNode parent)
        {
            int cursor = parent.ChildrenOffset, count = 0;
            while (cursor < parent.End)
            {
                if (++count > 1024) throw Bad("A version-information block has too many children.");
                if (parent.End - cursor < 6)
                {
                    for (int i = cursor; i < parent.End; i++) if (data[i] != 0) throw Bad("Version-information padding is invalid.");
                    yield break;
                }
                var child = Node(data, cursor, parent.End);
                yield return child;
                cursor = Align4(child.End);
            }
        }
        static string Utf16(byte[] data, int offset, int length)
        {
            Need(data, offset, length);
            try { return new UnicodeEncoding(false, false, true).GetString(data, offset, length); }
            catch (DecoderFallbackException) { throw Bad("Version information contains invalid Unicode."); }
        }
        static int Align4(int value) => (value + 3) & ~3;
        static byte[] Bytes(Stream stream, long offset, int length)
        {
            if (offset < 0 || length < 0 || offset > stream.Length - length) throw Bad("The PE is truncated.");
            var data = new byte[length];
            stream.Position = offset;
            int done = 0;
            while (done < length)
            {
                int read = stream.Read(data, done, length - done);
                if (read == 0) throw Bad("The PE ended unexpectedly.");
                done += read;
            }
            return data;
        }
        static void Need(byte[] data, int offset, int length)
        {
            if (offset < 0 || length < 0 || offset > data.Length - length) throw Bad("A resource offset is outside the PE data.");
        }
        static ushort U16(byte[] data, int offset) { Need(data, offset, 2); return (ushort)(data[offset] | data[offset + 1] << 8); }
        static uint U32(byte[] data, int offset) { Need(data, offset, 4); return (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24); }
        static InvalidDataException Bad(string message) => new InvalidDataException(message);
    }
}
