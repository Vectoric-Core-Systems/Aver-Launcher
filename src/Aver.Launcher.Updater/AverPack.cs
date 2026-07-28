using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Aver.Launcher.Core;

namespace Aver.Launcher.Updater;

/// <summary>One file's blob inside a pack.</summary>
public sealed class PackEntry
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;

    /// <summary>Uncompressed size.</summary>
    [JsonPropertyName("size")] public long Size { get; set; }

    /// <summary>Offset of the blob in the pack.</summary>
    [JsonPropertyName("off")] public long Offset { get; set; }

    /// <summary>Stored length at <see cref="Offset"/>, after compression.</summary>
    [JsonPropertyName("len")] public long Length { get; set; }

    /// <summary><c>brotli</c> or <c>raw</c>.</summary>
    [JsonPropertyName("codec")] public string Codec { get; set; } = "brotli";
}

/// <summary>The pack's index, stored at the end.</summary>
public sealed class PackIndex
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = 1;
    [JsonPropertyName("edition")] public string Edition { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("entries")] public List<PackEntry> Entries { get; set; } = [];
}

/// <summary>
/// The <c>.averpack</c> container.
/// </summary>
/// <remarks>
/// <para>
/// Layout: a 32-byte header, then blobs, then a Brotli'd JSON index, then a SHA-256 of everything
/// above.
/// </para>
/// <code>
///   0   8   magic "AVERPACK"
///   8   2   u16 format version
///  10   2   u16 flags (reserved)
///  12   4   u32 reserved
///  16   8   u64 index offset
///  24   8   u64 index length
///  32  ...  blobs, back to back
///  ...      Brotli(JSON index)
///  ...  32  SHA-256 of all preceding bytes
/// </code>
/// <para>
/// The index sits at the END, pointed to by the header, for one reason: it lets the publisher stream
/// blobs out without a second pass to fix up offsets, and it lets the launcher read the index with
/// two small ranged GETs (header, then index) before deciding what else to fetch.
/// </para>
/// <para>
/// That is what makes a full pack usable as a delta. Whole-file SHA-256 identity means the launcher
/// already knows which files it lacks; the index gives their byte ranges; it fetches only those. No
/// delta encoding, no chunk store, and nothing to publish per version pair.
/// </para>
/// </remarks>
public static class AverPack
{
    public const int HeaderSize = 32;
    public const int TrailerSize = 32;
    private static readonly byte[] Magic = "AVERPACK"u8.ToArray();

    /// <summary>Writes a pack from a staged directory.</summary>
    /// <param name="root">Staged payload root.</param>
    /// <param name="files">Files to include, in the order they should be stored.</param>
    public static PackIndex Write(Stream output, string root, IEnumerable<FileEntry> files,
                                  string edition, string version)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(files);

        var index = new PackIndex { Edition = edition, Version = version };

        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        output.Write(header);   // placeholder; rewritten once the index offset is known

        foreach (FileEntry f in files)
        {
            string full = Path.Combine(root, f.Path);
            long off = output.Position;

            using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            using (FileStream src = File.OpenRead(full))
            {
                src.CopyTo(brotli);
            }

            long len = output.Position - off;
            index.Entries.Add(new PackEntry
            {
                Path = f.Path,
                Sha256 = f.Sha256,
                Size = f.Size,
                Offset = off,
                Length = len,
                Codec = "brotli",
            });
        }

        long indexOffset = output.Position;
        byte[] indexJson = Encoding.UTF8.GetBytes(FeedJson.Write(index));
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            brotli.Write(indexJson);
        }
        long indexLength = output.Position - indexOffset;

        // Header, now that the offsets are known.
        output.Position = 0;
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], (ulong)indexOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], (ulong)indexLength);
        output.Write(header);

        // Trailing digest over everything written so far.
        output.Position = 0;
        byte[] digest;
        using (var sha = SHA256.Create())
        {
            digest = sha.ComputeHash(output);
        }
        output.Position = output.Length;
        output.Write(digest);

        return index;
    }

    /// <summary>Reads the header, returning the index's offset and length.</summary>
    public static (long Offset, long Length) ReadHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize) throw new InvalidDataException("pack header truncated");
        if (!header[..8].SequenceEqual(Magic)) throw new InvalidDataException("not an .averpack");
        ushort fmt = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        if (fmt != 1) throw new InvalidDataException($"pack format version {fmt} is newer than this launcher understands");
        return ((long)BinaryPrimitives.ReadUInt64LittleEndian(header[16..]),
                (long)BinaryPrimitives.ReadUInt64LittleEndian(header[24..]));
    }

    /// <summary>Decodes a Brotli'd index blob.</summary>
    public static PackIndex ReadIndex(ReadOnlySpan<byte> compressed)
    {
        using var src = new MemoryStream(compressed.ToArray());
        using var brotli = new BrotliStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        brotli.CopyTo(dst);
        return FeedJson.Read<PackIndex>(Encoding.UTF8.GetString(dst.ToArray()))
               ?? throw new InvalidDataException("pack index did not parse");
    }

    /// <summary>Decompresses one entry's blob into a file.</summary>
    public static void ExtractEntry(ReadOnlySpan<byte> blob, PackEntry entry, string destination)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        using FileStream dst = File.Create(destination);
        if (entry.Codec == "raw")
        {
            dst.Write(blob);
        }
        else
        {
            using var src = new MemoryStream(blob.ToArray());
            using var brotli = new BrotliStream(src, CompressionMode.Decompress);
            brotli.CopyTo(dst);
        }
    }

    /// <summary>Reads a whole pack's index from a local file.</summary>
    public static PackIndex ReadIndexFromFile(string path)
    {
        using FileStream fs = File.OpenRead(path);
        Span<byte> header = stackalloc byte[HeaderSize];
        fs.ReadExactly(header);
        (long off, long len) = ReadHeader(header);
        fs.Position = off;
        byte[] buf = new byte[len];
        fs.ReadExactly(buf);
        return ReadIndex(buf);
    }

    /// <summary>Lower-case hex SHA-256 of a file.</summary>
    public static string HashFile(string path)
    {
        using FileStream fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(fs));
    }
}
