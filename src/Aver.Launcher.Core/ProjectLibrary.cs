namespace Aver.Launcher.Core;

/// <summary>
/// The engine chosen for a project, and what it still cannot do.
/// </summary>
/// <param name="Install">Best available engine, or null when none can open the project at all.</param>
/// <param name="Missing">
/// Requirements this engine does not satisfy. Empty means the project will work fully. Non-empty
/// means it will OPEN and then misbehave in a specific, nameable way.
/// </param>
public sealed record EngineChoice(EngineInstall? Install, IReadOnlyList<RequirementEvidence> Missing)
{
    public bool FullySatisfied => Install is not null && Missing.Count == 0;
}

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

    /// <summary>
    /// What this project needs, inferred from its scripts and content. Computed once and cached,
    /// because it reads the whole Content tree.
    /// </summary>
    public ProjectRequirements Requirements => _requirements ??=
        Desc is null ? new ProjectRequirements() : ProjectRequirements.Infer(Desc);

    private ProjectRequirements? _requirements;

    /// <summary>Picks the engine that should open this project, or null when none qualifies.</summary>
    /// <remarks>
    /// Ordered by: satisfies the project's inferred requirements, then newest version, then most
    /// modules, then id for determinism.
    /// <para>
    /// Capability comes FIRST, ahead of version, because a build that lacks physics does not fail --
    /// it opens the project, renders the level, and then does nothing when Play is pressed. A newer
    /// engine that silently breaks the game is worse than a slightly older one that runs it.
    /// </para>
    /// <para>
    /// The module tie-break still matters below that: a fuller edition does everything a leaner one
    /// can, so preferring it is never wrong. Sorting by edition NAME would decide this alphabetically,
    /// with "minimal" beating "standard" -- which is exactly how that bug got in.
    /// </para>
    /// </remarks>
    public EngineInstall? BestEngine(IEnumerable<EngineInstall> installs)
        => Resolve(installs).Install;

    /// <summary>
    /// Picks an engine and reports what it still cannot do.
    /// </summary>
    /// <remarks>
    /// Deliberately returns a best-effort install even when nothing satisfies the project fully. The
    /// user may well want to open it anyway -- to look at the level, or to edit content that does not
    /// need the missing module -- so the launcher states the consequence and lets them decide, rather
    /// than refusing and leaving them with a disabled button and no explanation.
    /// </remarks>
    public EngineChoice Resolve(IEnumerable<EngineInstall> installs)
    {
        ArgumentNullException.ThrowIfNull(installs);
        if (Desc is null) return new EngineChoice(null, []);

        ProjectRequirements req = Requirements;

        EngineInstall? best = installs
            .Where(i => i.IsUsable && i.Edition.CanEditProjects && i.CanOpen(Desc))
            .OrderByDescending(i => req.SatisfiedBy(i.Edition))
            .ThenByDescending(i => i.Version, Comparer<string>.Create(AverVersion.Compare))
            .ThenByDescending(i => i.Edition.IncludedModules.Count)
            .ThenBy(i => i.Edition.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return new EngineChoice(best, best is null ? [] : req.MissingIn(best.Edition));
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
