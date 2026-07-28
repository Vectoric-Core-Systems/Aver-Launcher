namespace Aver.Launcher.Core;

/// <summary>
/// Reads the editor's recent-projects list, ported from <c>sandbox/src/ProjectBrowser.cpp</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>READ-ONLY, DELIBERATELY.</b> The engine's project browser rewrites this file WHOLESALE with no
/// locking, on a schedule the launcher cannot see. Two unsynchronised writers over one small file is
/// how entries get lost, and on an unlucky interleave how the file gets truncated. So the launcher
/// consumes it to enrich its own list and never writes it -- its own library lives in its own store,
/// where it owns the format and the lifetime.
/// </para>
/// <para>
/// There is no <c>Write</c> method here and adding one is a design change, not a small convenience.
/// </para>
/// </remarks>
public static class RecentProjects
{
    /// <summary>
    /// The engine caps the list at 10 while READING, so entries past the tenth surviving line are
    /// never seen. Matched so the launcher shows the same set the editor would.
    /// </summary>
    public const int MaxRecents = 10;

    /// <summary><c>%LOCALAPPDATA%\AverEngine</c> -- the engine's <c>userDataDir()</c>.</summary>
    public static string UserDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AverEngine");

    /// <summary><c>%LOCALAPPDATA%\AverEngine\recent.txt</c>.</summary>
    public static string RecentsPath => Path.Combine(UserDataDir, "recent.txt");

    /// <summary>
    /// Reads the list, most-recent-first, dropping entries whose file has gone.
    /// </summary>
    /// <param name="fileExists">
    /// Existence probe, injectable so the parsing rules can be tested without touching the disk.
    /// Defaults to <see cref="File.Exists"/>.
    /// </param>
    public static IReadOnlyList<string> Read(string? path = null, Func<string, bool>? fileExists = null)
    {
        path ??= RecentsPath;
        fileExists ??= File.Exists;

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A missing list is the normal first-run state, not an error worth surfacing.
            return [];
        }

        return Parse(text, fileExists);
    }

    /// <summary>Parses the file's text. Exposed so the exact trimming rules are testable.</summary>
    public static IReadOnlyList<string> Parse(string text, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;

        foreach (string raw in text.Split('\n'))
        {
            if (result.Count >= MaxRecents) break;

            // The engine pops only '\r' and ' ' off the END. Not tabs, and nothing from the front.
            // Using Trim() here would accept lines the editor would reject, so the launcher would
            // list a project the editor's own browser does not.
            string line = raw.TrimEnd('\r', ' ');
            if (line.Length == 0) continue;

            // A project the user moved or deleted is a dead row that errors when clicked, so it is
            // dropped on read rather than shown and then refused.
            if (fileExists(line)) result.Add(line);
        }

        return result;
    }

    /// <summary>
    /// The display name the editor shows for a manifest path: the file name without its extension.
    /// </summary>
    /// <remarks>
    /// This is the manifest's STEM, not the <c>NAME</c> key, matching <c>displayName()</c> in
    /// ProjectBrowser.cpp. The two can differ -- a renamed folder leaves the key untouched -- and the
    /// launcher shows the same string the editor does so one project does not appear under two names.
    /// </remarks>
    public static string DisplayName(string manifestPath)
        => Path.GetFileNameWithoutExtension(manifestPath) ?? string.Empty;

    /// <summary>
    /// The default projects root: <c>Documents\Aver Projects</c>, from the OS known folder.
    /// </summary>
    /// <remarks>
    /// Asked of the OS rather than composed from <c>%USERPROFILE%</c>, because Documents is commonly
    /// redirected into OneDrive and the engine resolves it the same way (<c>FOLDERID_Documents</c>).
    /// Projects are SIBLINGS of the engine, never inside it.
    /// </remarks>
    public static string DefaultProjectsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Aver Projects");
}
