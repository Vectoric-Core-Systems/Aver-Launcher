using Aver.Launcher.Core;

namespace Aver.Launcher.Updater;

/// <summary>Outcome of verifying an install against what was recorded when it landed.</summary>
public sealed class VerifyReport
{
    public int Checked { get; init; }
    public List<string> Missing { get; } = [];
    public List<string> Corrupt { get; } = [];

    /// <summary>Set when the install has no recorded state, so nothing could be checked.</summary>
    public string? Unverifiable { get; init; }

    public bool Ok => Unverifiable is null && Missing.Count == 0 && Corrupt.Count == 0;

    public string Summary => Unverifiable is not null
        ? Unverifiable
        : Ok
            ? $"All {Checked} files match."
            : $"{Missing.Count} missing, {Corrupt.Count} corrupt of {Checked} checked.";
}

/// <summary>Verify and uninstall.</summary>
public static class InstallMaintenance
{
    /// <summary>
    /// Re-hashes every file against <c>install-state.json</c>.
    /// </summary>
    /// <remarks>
    /// Hashes rather than sizes and timestamps: this exists to catch the cases those miss -- a file
    /// truncated by a full disk, altered by antivirus quarantine and restore, or a hard link written
    /// through from another install. It reads the whole install, so it is a deliberate user action
    /// rather than something that happens on a timer.
    /// </remarks>
    public static VerifyReport Verify(EngineInstall install, IProgress<double>? progress = null,
                                      CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(install);

        string statePath = Path.Combine(install.Root, "install-state.json");
        if (!File.Exists(statePath))
        {
            return new VerifyReport
            {
                Unverifiable = "This install has no recorded file list, so it cannot be verified. "
                               + "It was staged by hand rather than downloaded.",
            };
        }

        InstallState? state;
        try
        {
            state = FeedJson.Read<InstallState>(File.ReadAllText(statePath));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return new VerifyReport { Unverifiable = $"The recorded file list could not be read: {ex.Message}" };
        }

        if (state is null || state.Files.Count == 0)
        {
            return new VerifyReport { Unverifiable = "The recorded file list is empty." };
        }

        var report = new VerifyReport { Checked = state.Files.Count };
        for (int i = 0; i < state.Files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            FileEntry f = state.Files[i];
            string p = Path.Combine(install.Root, f.Path);

            if (!File.Exists(p))
            {
                report.Missing.Add(f.Path);
            }
            else
            {
                try
                {
                    if (!string.Equals(AverPack.HashFile(p), f.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        report.Corrupt.Add(f.Path);
                    }
                }
                catch (IOException)
                {
                    report.Corrupt.Add(f.Path);
                }
            }

            progress?.Report((i + 1) / (double)state.Files.Count);
        }

        return report;
    }

    /// <summary>
    /// Removes an install.
    /// </summary>
    /// <remarks>
    /// Deletes files, which decrements their hard-link count. Truncating or overwriting would corrupt
    /// every OTHER install sharing those bytes -- roughly 17.5 MB of each payload is shared -- so
    /// "empty the files then remove the directory" is exactly the wrong shape here.
    /// <para>
    /// Renamed out of the way first so a failure part-way cannot leave a half-deleted install that
    /// still looks installable. The rename succeeds even when a file inside is mapped by a running
    /// process; the delete that follows is what would fail, and then it fails against a directory
    /// already moved aside.
    /// </para>
    /// </remarks>
    public static void Uninstall(EngineInstall install, IFileSystemOps ops)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(ops);

        string retired = install.Root + ".removing-" + Guid.NewGuid().ToString("N")[..8];
        ops.MoveDirectory(install.Root, retired);

        try
        {
            Directory.Delete(retired, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Moved aside and so no longer listed; the bytes go on the next sweep rather than
            // leaving the user with a half-removed install that still appears in the list.
            throw new IOException(
                $"The install was removed from the list, but its files are still in use and remain at "
                + $"{retired}. Close anything using them and delete that folder.", ex);
        }
    }

    /// <summary>Deletes leftovers from interrupted installs and removals under a root.</summary>
    public static int SweepLeftovers(string installRoot)
    {
        if (!Directory.Exists(installRoot)) return 0;
        int swept = 0;

        foreach (string editionDir in Directory.EnumerateDirectories(installRoot))
        {
            foreach (string d in Directory.EnumerateDirectories(editionDir))
            {
                string name = Path.GetFileName(d);
                if (!name.Contains(".staging-", StringComparison.Ordinal)
                    && !name.Contains(".removing-", StringComparison.Ordinal)
                    && !name.Contains(".old-", StringComparison.Ordinal))
                {
                    continue;
                }

                try { Directory.Delete(d, recursive: true); swept++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        return swept;
    }
}
