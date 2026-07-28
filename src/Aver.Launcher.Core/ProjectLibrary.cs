namespace Aver.Launcher.Core;

/// <summary>A project the launcher can show and open.</summary>
public sealed class ProjectEntry
{
    public required string ManifestPath { get; init; }

    /// <summary>The manifest's file stem, which is what the editor's own browser shows.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Parsed manifest, or null when it could not be read.</summary>
    public ProjectDesc? Desc { get; init; }

    /// <summary>Why the manifest could not be read, when it could not.</summary>
    public string? Error { get; init; }

    /// <summary>True when this came from the editor's recent list rather than a folder scan.</summary>
    public bool IsRecent { get; init; }

    public DateTime LastModifiedUtc { get; init; }

    public string Directory => Path.GetDirectoryName(ManifestPath) ?? string.Empty;

    /// <summary>The engine floor the manifest declares, or empty when it declares none.</summary>
    public string EngineMinVersion => Desc?.EngineMinVersion ?? string.Empty;

    /// <summary>Picks the engine that should open this project, or null when none qualifies.</summary>
    /// <remarks>
    /// Newest version first, then the edition with the MOST optional modules.
    /// <para>
    /// The module tie-break is the part that matters. A fuller edition can do everything a leaner one
    /// can, so preferring it is never wrong; preferring the leaner one silently opens a project in an
    /// engine without physics or global illumination, and the author sees a world that behaves
    /// differently for no stated reason. Sorting editions by name would decide this alphabetically --
    /// "minimal" beating "standard" -- which is how that bug got in.
    /// </para>
    /// <para>
    /// A manifest cannot yet say which modules it needs; <c>.ocproject</c> has no such key, and unknown
    /// keys are ignored by design. Until it can, "most capable that satisfies the version floor" is the
    /// only defensible default.
    /// </para>
    /// </remarks>
    public EngineInstall? BestEngine(IEnumerable<EngineInstall> installs)
    {
        ArgumentNullException.ThrowIfNull(installs);
        if (Desc is null) return null;

        return installs
            .Where(i => i.IsUsable && i.Edition.CanEditProjects && i.CanOpen(Desc))
            .OrderByDescending(i => i.Version, Comparer<string>.Create(AverVersion.Compare))
            .ThenByDescending(i => i.Edition.IncludedModules.Count)
            .ThenBy(i => i.Edition.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}

/// <summary>
/// Assembles the project list from the editor's recent file and the projects root.
/// </summary>
/// <remarks>
/// <c>recent.txt</c> is read and never written. The editor rewrites it wholesale with no locking on a
/// schedule the launcher cannot see, so a second writer loses entries and, on a bad interleave,
/// truncates the file. The launcher's own ordering lives in its own store.
/// </remarks>
public static class ProjectLibrary
{
    /// <summary>
    /// Loads projects, recents first, then anything else found under the projects root.
    /// </summary>
    /// <param name="projectsRoot">Defaults to <c>Documents\Aver Projects</c>.</param>
    /// <param name="recentsPath">Defaults to the editor's <c>recent.txt</c>.</param>
    public static IReadOnlyList<ProjectEntry> Load(string? projectsRoot = null, string? recentsPath = null)
    {
        projectsRoot ??= RecentProjects.DefaultProjectsRoot;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ProjectEntry>();

        foreach (string path in RecentProjects.Read(recentsPath))
        {
            if (seen.Add(Normalise(path))) entries.Add(Describe(path, isRecent: true));
        }

        foreach (string path in ScanRoot(projectsRoot))
        {
            if (seen.Add(Normalise(path))) entries.Add(Describe(path, isRecent: false));
        }

        return entries;
    }

    /// <summary>Finds <c>.ocproject</c> manifests one and two levels below a root.</summary>
    /// <remarks>
    /// Not a full recursive walk. A project's own Content tree can be large, and descending it to look
    /// for manifests that convention says are at the top would make startup cost scale with asset count.
    /// </remarks>
    public static IReadOnlyList<string> ScanRoot(string root)
    {
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;

        try
        {
            found.AddRange(Directory.EnumerateFiles(root, "*.ocproject", SearchOption.TopDirectoryOnly));
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                try
                {
                    found.AddRange(Directory.EnumerateFiles(dir, "*.ocproject", SearchOption.TopDirectoryOnly));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return found;
    }

    private static ProjectEntry Describe(string manifestPath, bool isRecent)
    {
        // engineVersion null: the library must LIST a project it cannot currently open and say why,
        // rather than hide it. The version gate is applied per-install when choosing what to launch.
        bool ok = OcProjectReader.TryLoad(manifestPath, engineVersion: null, out ProjectDesc? desc, out string? err);

        DateTime modified = DateTime.MinValue;
        try { modified = File.GetLastWriteTimeUtc(manifestPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }

        return new ProjectEntry
        {
            ManifestPath = manifestPath,
            DisplayName = RecentProjects.DisplayName(manifestPath),
            Desc = ok ? desc : null,
            Error = ok ? null : err,
            IsRecent = isRecent,
            LastModifiedUtc = modified,
        };
    }

    private static string Normalise(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
