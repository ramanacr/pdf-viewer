using System.IO;
using System.Text;

namespace ReleaseGate.Tool;

/// <summary>
/// Reads the file list of a .NET single-file bundle, so a size report can see inside
/// PdfViewer.exe - which is nearly the whole payload - instead of reporting one opaque number.
///
/// Layout (Microsoft.NET.HostModel, bundle format 2.0 and later): the apphost carries an 8-byte
/// header offset immediately before a fixed 32-byte signature (the SHA-256 of ".net core
/// bundle"). At that offset: major and minor version (uint32 each), the file count (int32), the
/// bundle id (length-prefixed string), then from version 2 the deps.json and runtimeconfig.json
/// locations (4 x int64) and flags (uint64), then per file: offset and size (int64 each), from
/// version 6 the compressed size (int64, 0 when stored uncompressed), a type byte and the
/// length-prefixed relative path. Anything that does not fit this is reported as "not a bundle".
/// </summary>
internal static class BundleManifest
{
    internal static readonly byte[] Signature =
    {
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    };

    /// <summary>Bundled files by relative path, with the bytes each occupies in the executable; null when it is not a bundle.</summary>
    public static SortedDictionary<string, long>? TryRead(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return TryRead(bytes);
    }

    public static SortedDictionary<string, long>? TryRead(byte[] bytes)
    {
        int at = bytes.AsSpan().IndexOf(Signature);
        if (at < 8) return null;

        long headerOffset = BitConverter.ToInt64(bytes, at - 8);
        if (headerOffset <= 0 || headerOffset >= bytes.Length) return null; // an apphost that was never bundled

        try
        {
            using var reader = new BinaryReader(new MemoryStream(bytes, writable: false), Encoding.UTF8);
            reader.BaseStream.Position = headerOffset;

            uint major = reader.ReadUInt32();
            reader.ReadUInt32(); // minor
            int count = reader.ReadInt32();
            if (major is < 1 or > 100 || count is < 0 or > 100_000) return null;
            reader.ReadString(); // bundle id

            if (major >= 2)
            {
                for (int i = 0; i < 4; i++) reader.ReadInt64(); // deps.json, runtimeconfig.json
                reader.ReadUInt64(); // flags
            }

            var files = new SortedDictionary<string, long>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                long offset = reader.ReadInt64();
                long size = reader.ReadInt64();
                long compressed = major >= 6 ? reader.ReadInt64() : 0;
                reader.ReadByte(); // type
                string rel = reader.ReadString().Replace('\\', '/');

                long stored = compressed > 0 ? compressed : size;
                if (offset < 0 || stored < 0 || offset + stored > bytes.Length) return null;
                files[rel] = stored;
            }

            return files;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
