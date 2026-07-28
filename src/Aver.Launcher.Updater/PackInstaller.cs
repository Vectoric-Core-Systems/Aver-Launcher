using Aver.Launcher.Core;

namespace Aver.Launcher.Updater;

/// <summary>Progress from an install.</summary>
/// <param name="Phase">Human-readable stage.</param>
/// <param name="BytesDone">Bytes fetched so far.</param>
/// <param name="BytesTotal">Bytes that will be fetched (NOT the installed size).</param>
public readonly record struct InstallProgress(string Phase, long BytesDone, long BytesTotal)
{
    public double Fraction => BytesTotal <= 0 ? 0 : Math.Clamp((double)BytesDone / BytesTotal, 0, 1);
}

/// <summary>What an install actually did, for reporting and for the gates.</summary>
public sealed class InstallReport
{
    public required string Root { get; init; }
    public int FilesTotal { get; init; }
    public int FilesReused { get; init; }
    public int FilesFetched { get; init; }

    /// <summary>Compressed bytes actually pulled from the pack.</summary>
    public long BytesFetched { get; init; }

    /// <summary>Size of the whole pack, for comparison.</summary>
    public long PackSize { get; init; }

    public long BytesInstalled { get; init; }

    /// <summary>Fraction of the pack that had to be transferred. The headline number.</summary>
    public double WireFraction => PackSize <= 0 ? 0 : (double)BytesFetched / PackSize;
}

/// <summary>Filesystem operations that differ by platform, injected so the applier stays testable.</summary>
public interface IFileSystemOps
{
    /// <summary>Hard-links <paramref name="link"/> to <paramref name="existing"/>. False if unsupported.</summary>
    bool TryHardLink(string existing, string link);

    /// <summary>Renames a directory. Must be atomic within a volume.</summary>
    void MoveDirectory(string from, string to);
}

/// <summary>Portable fallback: copies instead of linking.</summary>
public sealed class PortableFileSystemOps : IFileSystemOps
{
    public bool TryHardLink(string existing, string link) => false;

    public void MoveDirectory(string from, string to) => Directory.Move(from, to);
}

/// <summary>
/// Installs a version from a pack, fetching only the files this machine does not already have.
/// </summary>
/// <remarks>
/// <para>
/// The deduplication is whole-file SHA-256 against every OTHER installed version. About 17.5 MB of
/// the ~21 MB payload -- dxcompiler.dll alone is 14.3 MB, plus dxil, nethost, both Roboto faces and
/// the artwork -- is byte-identical between builds, so a second version costs a fraction of the
/// first. No chunk store, no delta encoding, no per-version-pair publishing.
/// </para>
/// <para>
/// Reused files are HARD-LINKED, so five installed versions cost the shared bytes once. That makes
/// two rules non-negotiable: an install directory is read-only to everything but the installer, and
/// uninstall must DELETE files (decrementing the link count) rather than truncate them.
/// </para>
/// <para>
/// Everything lands in a sibling staging directory and is verified before anything user-visible
/// moves. The final step is a directory rename, which is atomic on NTFS and -- measured, not
/// assumed -- succeeds even while another process has a DLL inside the target mapped.
/// </para>
/// </remarks>
public sealed class PackInstaller(IFileSystemOps? ops = null)
{
    private readonly IFileSystemOps _ops = ops ?? new PortableFileSystemOps();

    /// <summary>Builds a sha256 -> existing local path map from other installs of any edition.</summary>
    public static Dictionary<string, string> BuildLocalInventory(IEnumerable<EngineInstall> installs)
    {
        ArgumentNullException.ThrowIfNull(installs);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (EngineInstall i in installs)
        {
            string stateFile = Path.Combine(i.Root, "install-state.json");
            if (!File.Exists(stateFile)) continue;
            InstallState? state = FeedJson.Read<InstallState>(File.ReadAllText(stateFile));
            if (state is null) continue;

            foreach (FileEntry f in state.Files)
            {
                string full = Path.Combine(i.Root, f.Path);
                // Trust the recorded hash only if the file is still there and the size matches.
                // Re-hashing every candidate would cost a full read of every install on every update.
                if (File.Exists(full) && new FileInfo(full).Length == f.Size)
                {
                    map.TryAdd(f.Sha256, full);
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Installs a version. <paramref name="fetch"/> is handed the byte ranges to retrieve.
    /// </summary>
    public async Task<InstallReport> InstallAsync(
        VersionManifest manifest,
        PackIndex packIndex,
        string installRoot,
        IReadOnlyDictionary<string, string> localInventory,
        IRangeFetcher fetch,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(packIndex);
        ArgumentNullException.ThrowIfNull(localInventory);
        ArgumentNullException.ThrowIfNull(fetch);

        string final = Path.Combine(installRoot, manifest.Edition, manifest.Version);
        string staging = final + ".staging-" + Guid.NewGuid().ToString("N")[..8];

        var byPath = packIndex.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);

        // Split the file list into what we can link and what must be fetched.
        var toFetch = new List<PackEntry>();
        var toLink = new List<(FileEntry File, string Source)>();
        foreach (FileEntry f in manifest.Files)
        {
            if (localInventory.TryGetValue(f.Sha256, out string? existing))
            {
                toLink.Add((f, existing));
            }
            else if (byPath.TryGetValue(f.Path, out PackEntry? e))
            {
                toFetch.Add(e);
            }
            else
            {
                throw new InvalidDataException(
                    $"{f.Path} is in the manifest but not in the pack index; the two disagree");
            }
        }

        long bytesToFetch = toFetch.Sum(e => e.Length);
        long done = 0;
        progress?.Report(new InstallProgress(
            $"{toLink.Count} file(s) already here, fetching {toFetch.Count}", 0, bytesToFetch));

        Directory.CreateDirectory(staging);
        try
        {
            // ---- reuse ----
            foreach ((FileEntry f, string source) in toLink)
            {
                ct.ThrowIfCancellationRequested();
                string dst = Path.Combine(staging, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (!_ops.TryHardLink(source, dst)) File.Copy(source, dst, overwrite: true);
            }

            // ---- fetch ----
            //
            // Ranges are coalesced: adjacent entries in the pack become one request. The publisher
            // orders entries so that files which change together sit together, which is what turns a
            // typical update into a couple of requests instead of dozens.
            foreach (RangeBatch batch in CoalesceRanges(toFetch))
            {
                ct.ThrowIfCancellationRequested();
                byte[] buffer = await fetch.FetchAsync(batch.Start, batch.Length, ct).ConfigureAwait(false);

                foreach (PackEntry e in batch.Entries)
                {
                    int rel = (int)(e.Offset - batch.Start);
                    AverPack.ExtractEntry(
                        buffer.AsSpan(rel, (int)e.Length), e, Path.Combine(staging, e.Path));
                }

                done += batch.Length;
                progress?.Report(new InstallProgress("downloading", done, bytesToFetch));
            }

            // ---- verify EVERY file before anything user-visible moves ----
            progress?.Report(new InstallProgress("verifying", bytesToFetch, bytesToFetch));
            foreach (FileEntry f in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                string p = Path.Combine(staging, f.Path);
                if (!File.Exists(p)) throw new InvalidDataException($"{f.Path} is missing after install");

                string actual = AverPack.HashFile(p);
                if (!string.Equals(actual, f.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"{f.Path} failed verification: expected {f.Sha256[..12]}, got {actual[..12]}");
                }
            }

            // ---- record what landed, so the next install can dedupe against it ----
            var state = new InstallState
            {
                Edition = manifest.Edition,
                Version = manifest.Version,
                EntryPoint = manifest.EntryPoint,
                InstalledUtc = DateTime.UtcNow.ToString("O"),
                Files = manifest.Files,
            };
            File.WriteAllText(Path.Combine(staging, "install-state.json"), FeedJson.Write(state));

            // payload.json keeps the shape the scanner already understands.
            var payload = new PayloadInfo
            {
                SchemaVersion = 1,
                EngineName = manifest.EngineName,
                Version = manifest.Version,
                Config = "Release",
                EntryPoint = manifest.EntryPoint,
                Options = manifest.Options,
                FileCount = manifest.Files.Count,
                TotalBytes = manifest.TotalBytes,
                StagedUtc = manifest.PublishedUtc,
            };
            File.WriteAllText(Path.Combine(staging, "payload.json"), FeedJson.Write(payload));

            // ---- atomic swap ----
            if (Directory.Exists(final))
            {
                string retired = final + ".old-" + Guid.NewGuid().ToString("N")[..8];
                _ops.MoveDirectory(final, retired);
                TryDelete(retired);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            _ops.MoveDirectory(staging, final);

            return new InstallReport
            {
                Root = final,
                FilesTotal = manifest.Files.Count,
                FilesReused = toLink.Count,
                FilesFetched = toFetch.Count,
                BytesFetched = bytesToFetch,
                PackSize = manifest.Pack.Size,
                BytesInstalled = manifest.TotalBytes,
            };
        }
        catch
        {
            // Nothing user-visible was touched until the swap, so cleanup is just the staging dir.
            TryDelete(staging);
            throw;
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal readonly record struct RangeBatch(long Start, long Length, List<PackEntry> Entries);

    /// <summary>
    /// Groups entries into contiguous-enough runs.
    /// </summary>
    /// <remarks>
    /// A gap smaller than <paramref name="gapTolerance"/> is downloaded rather than skipped, because
    /// one request for 64 KB beats two requests plus a round trip. Multi-range requests are NOT used:
    /// support for them on the CDN behind GitHub release assets is not guaranteed, and a coalesced
    /// single range works everywhere.
    /// </remarks>
    internal static List<RangeBatch> CoalesceRanges(List<PackEntry> entries, long gapTolerance = 262144)
    {
        var batches = new List<RangeBatch>();
        if (entries.Count == 0) return batches;

        List<PackEntry> ordered = [.. entries.OrderBy(e => e.Offset)];
        long start = ordered[0].Offset;
        long end = ordered[0].Offset + ordered[0].Length;
        var current = new List<PackEntry> { ordered[0] };

        for (int i = 1; i < ordered.Count; i++)
        {
            PackEntry e = ordered[i];
            if (e.Offset - end <= gapTolerance)
            {
                end = Math.Max(end, e.Offset + e.Length);
                current.Add(e);
            }
            else
            {
                batches.Add(new RangeBatch(start, end - start, current));
                start = e.Offset;
                end = e.Offset + e.Length;
                current = [e];
            }
        }
        batches.Add(new RangeBatch(start, end - start, current));
        return batches;
    }
}

/// <summary>Records what an install contains, so later installs can dedupe against it.</summary>
public sealed class InstallState
{
    public string Edition { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string EntryPoint { get; set; } = string.Empty;
    public string InstalledUtc { get; set; } = string.Empty;
    public List<FileEntry> Files { get; set; } = [];
}

/// <summary>Fetches a byte range of the pack. Local file, HTTP, or a test double.</summary>
public interface IRangeFetcher
{
    Task<byte[]> FetchAsync(long offset, long length, CancellationToken ct = default);
}
