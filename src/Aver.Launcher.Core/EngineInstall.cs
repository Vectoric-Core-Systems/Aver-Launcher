namespace Aver.Launcher.Core;

/// <summary>One engine version installed on this machine.</summary>
public sealed class EngineInstall
{
    /// <summary>Root of the install: the directory holding <c>bin\</c> and <c>scripting\</c>.</summary>
    public required string Root { get; init; }

    /// <summary>Absolute path to the editor executable.</summary>
    public required string EntryPoint { get; init; }

    public required string Version { get; init; }

    public required EngineEdition Edition { get; init; }

    /// <summary>The payload descriptor this install was staged with, when it has one.</summary>
    public PayloadInfo? Payload { get; init; }

    /// <summary>Bytes on disk, summed at scan time.</summary>
    public long SizeBytes { get; init; }

    /// <summary>When the entry point was last written -- a stand-in for "installed at".</summary>
    public DateTime InstalledUtc { get; init; }

    /// <summary>False when the payload is present but something it promised is missing.</summary>
    public bool IsUsable => File.Exists(EntryPoint);

    /// <summary>
    /// True when this build satisfies a project's <c>ENGINE &lt;name&gt; &lt;minVersion&gt;</c> floor.
    /// </summary>
    public bool CanOpen(ProjectDesc project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return AverVersion.IsEngineNameMatch(project.EngineName)
               && AverVersion.Satisfies(project.EngineMinVersion, Version);
    }

    /// <summary>Display label: "0.1.0 (standard)".</summary>
    public string Label => $"{Version} ({Edition.Id})";
}

/// <summary>
/// Finds engine installs on disk.
/// </summary>
/// <remarks>
/// <para>
/// The layout is <c>&lt;root&gt;\&lt;edition&gt;\&lt;version&gt;\</c>, and version directories are
/// treated as IMMUTABLE. An update installs a new one beside the old rather than mutating it, which is
/// what makes rollback "do nothing" and lets a running editor keep its files while a new version lands.
/// </para>
/// <para>
/// A directory is recognised by its <c>payload.json</c>, written by the engine's
/// <c>scripts/stage-payload.ps1</c>. That file is the only machine-readable record of which modules a
/// build contains -- the engine emits no generated config header, so nothing else can be asked.
/// </para>
/// </remarks>
public static class EngineInstallStore
{
    /// <summary>
    /// Default install root, per-user so installing needs no elevation:
    /// <c>%LOCALAPPDATA%\Aver\Engine</c>.
    /// </summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aver", "Engine");

    /// <summary>
    /// Scans an install root. Never throws: an unreadable directory is a missing install, not a crash
    /// at startup.
    /// </summary>
    public static IReadOnlyList<EngineInstall> Scan(string? root = null)
    {
        root ??= DefaultRoot;
        var found = new List<EngineInstall>();
        if (!Directory.Exists(root)) return found;

        foreach (string editionDir in SafeDirs(root))
        {
            foreach (string versionDir in SafeDirs(editionDir))
            {
                EngineInstall? install = Read(versionDir, Path.GetFileName(editionDir));
                if (install is not null) found.Add(install);
            }
        }

        // Newest first, and within a version the editions sort by id so the list is stable between runs.
        return found
            .OrderByDescending(i => i.Version, Comparer<string>.Create(AverVersion.Compare))
            .ThenBy(i => i.Edition.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reads a single install directory. Returns null when it is not one.
    /// </summary>
    public static EngineInstall? Read(string versionDir, string? editionId = null)
    {
        string payloadPath = Path.Combine(versionDir, "payload.json");
        if (!File.Exists(payloadPath)) return null;
        if (!PayloadInfo.TryLoad(payloadPath, out PayloadInfo? payload, out _)) return null;

        editionId ??= Path.GetFileName(Path.GetDirectoryName(versionDir) ?? string.Empty);
        if (string.IsNullOrEmpty(editionId)) editionId = "unknown";

        string entry = Path.Combine(versionDir, payload!.EntryPoint.Length > 0
            ? payload.EntryPoint
            : Path.Combine("bin", "Sandbox.exe"));

        long size = 0;
        DateTime installed = DateTime.MinValue;
        try
        {
            foreach (string f in Directory.EnumerateFiles(versionDir, "*", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(f);
                size += fi.Length;
            }
            if (File.Exists(entry)) installed = File.GetLastWriteTimeUtc(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A partially readable install still lists; the size is just approximate.
        }

        return new EngineInstall
        {
            Root = versionDir,
            EntryPoint = entry,
            Version = payload.Version.Length > 0 ? payload.Version : Path.GetFileName(versionDir),
            Edition = payload.ToEdition(editionId),
            Payload = payload,
            SizeBytes = size,
            InstalledUtc = installed,
        };
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
