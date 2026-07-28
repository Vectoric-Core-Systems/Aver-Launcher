using System.Diagnostics.CodeAnalysis;

namespace Aver.Launcher.Core;

/// <summary>
/// A parsed <c>.ocproject</c> manifest. Mirrors the engine's <c>aver::fmt::ProjectDesc</c>
/// (<c>modules/formats/include/aver/formats/OcProject.hpp</c>).
/// </summary>
public sealed class ProjectDesc
{
    /// <summary><c>OCPROJECT &lt;n&gt;</c>. Defaults to 1.</summary>
    public int Version { get; set; } = 1;

    /// <summary><c>NAME</c> -- the only required field besides the header.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>First token of <c>ENGINE</c>. Must name this engine, case-insensitively.</summary>
    public string EngineName { get; set; } = string.Empty;

    /// <summary>Second token of <c>ENGINE</c>. A minimum, not a pin.</summary>
    public string EngineMinVersion { get; set; } = string.Empty;

    /// <summary><c>CONTENT</c>, relative to the manifest. Defaults to <c>Content</c>.</summary>
    public string ContentRoot { get; set; } = "Content";

    /// <summary><c>STARTMAP</c>, relative to the content root. May name a file that does not exist.</summary>
    public string StartMap { get; set; } = string.Empty;

    /// <summary><c>AUTHOR</c> -- free text to end of line.</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>Absolute directory holding the manifest.</summary>
    public string Dir { get; set; } = string.Empty;

    /// <summary>Absolute path of the manifest itself.</summary>
    public string ManifestPath { get; set; } = string.Empty;

    /// <summary>
    /// Keys the manifest carried that this build does not understand, preserved for diagnostics.
    /// </summary>
    /// <remarks>
    /// The engine discards these; the launcher keeps them because it is the component most likely to
    /// meet a manifest written by a NEWER engine than the one being used to open it, and "this
    /// project mentions PLUGINS, which this build ignores" is worth being able to say. They are
    /// still ignored for every decision -- collecting them must not become enforcing them.
    /// </remarks>
    public List<string> UnknownKeys { get; } = [];

    // Derived paths. Deliberately not manifest fields -- the engine's header notes build output
    // location "is not a choice", and Binaries\ sits outside Content\ because build output is
    // neither content nor something a shipped game reads.
    public string ContentDir => Path.Combine(Dir, ContentRoot);
    public string ScriptsDir => Path.Combine(Dir, ContentRoot, "Scripts");
    public string BinariesDir => Path.Combine(Dir, "Binaries");
}

/// <summary>
/// Reader for the <c>.ocproject</c> text format, ported from <c>modules/formats/src/OcProject.cpp</c>
/// and the <c>detail/TextScan.hpp</c> helpers it uses.
/// </summary>
/// <remarks>
/// Faithfulness matters more than elegance here. Three rules are load-bearing and easy to get
/// subtly wrong:
/// <list type="number">
///   <item><description>
///     A leading UTF-8 BOM is stripped. Manifests get hand-edited, and both Notepad and
///     PowerShell's <c>Out-File -Encoding utf8</c> write one. Left in place it becomes part of the
///     first token and the file reads as "not a project".
///   </description></item>
///   <item><description>
///     Unknown keys are ignored SILENTLY. <c>docs/PROJECTS.md</c> calls this "a guarantee the loader
///     keeps, not an aspiration", so the launcher must not reject a manifest from a later engine.
///   </description></item>
///   <item><description>
///     Whitespace is exactly the six ASCII characters <c>isSpace</c> accepts. Using
///     <c>char.IsWhiteSpace</c> would also split on U+00A0 and friends and diverge from the engine
///     on any manifest that picked one up from a copy-paste.
///   </description></item>
/// </list>
/// </remarks>
public static class OcProjectReader
{
    /// <summary>The engine's <c>isSpace</c>: six ASCII characters, nothing Unicode.</summary>
    private static bool IsSpace(char c)
        => c is ' ' or '\t' or '\r' or '\n' or '\v' or '\f';

    private static ReadOnlySpan<char> Trim(ReadOnlySpan<char> s)
    {
        int b = 0, e = s.Length;
        while (b < e && IsSpace(s[b])) b++;
        while (e > b && IsSpace(s[e - 1])) e--;
        return s[b..e];
    }

    /// <summary>Truncate at the first '#'.</summary>
    private static ReadOnlySpan<char> TruncateHash(ReadOnlySpan<char> s)
    {
        int h = s.IndexOf('#');
        return h < 0 ? s : s[..h];
    }

    /// <summary>One trailing ';' stripped, then re-trimmed.</summary>
    private static ReadOnlySpan<char> StripTrailingSemicolon(ReadOnlySpan<char> s)
    {
        s = Trim(s);
        if (s.Length > 0 && s[^1] == ';') s = Trim(s[..^1]);
        return s;
    }

    private static List<string> SplitWhitespace(ReadOnlySpan<char> s)
    {
        var outv = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && IsSpace(s[i])) i++;
            int start = i;
            while (i < s.Length && !IsSpace(s[i])) i++;
            if (i > start) outv.Add(s[start..i].ToString());
        }
        return outv;
    }

    /// <summary>
    /// The engine's <c>parseI32</c>, which is <c>std::from_chars</c>: it parses a LEADING integer
    /// and succeeds on a prefix, so "1abc" is 1 rather than the fallback.
    /// </summary>
    private static int ParseI32(string s, int fallback)
    {
        ReadOnlySpan<char> t = Trim(s);
        int i = 0;
        bool neg = false;
        if (i < t.Length && (t[i] == '-' || t[i] == '+'))
        {
            neg = t[i] == '-';
            i++;
        }

        int digits = 0;
        long v = 0;
        while (i < t.Length && t[i] >= '0' && t[i] <= '9')
        {
            v = (v * 10) + (t[i] - '0');
            i++;
            digits++;
            if (v > int.MaxValue) return fallback;   // from_chars reports result_out_of_range
        }

        if (digits == 0) return fallback;
        return neg ? (int)-v : (int)v;
    }

    /// <summary>Parses manifest text. Returns false with a reason for the two hard errors only.</summary>
    public static bool TryParse(string text, ProjectDesc desc, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(desc);
        text ??= string.Empty;

        // A char-level BOM: File.ReadAllText already decodes a byte-order mark to U+FEFF only when
        // it could not be consumed as an encoding preamble, so both shapes are handled.
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        bool sawHeader = false;
        desc.UnknownKeys.Clear();

        foreach (string rawLine in text.Split('\n'))
        {
            ReadOnlySpan<char> line = StripTrailingSemicolon(TruncateHash(rawLine));
            if (line.IsEmpty) continue;

            List<string> t = SplitWhitespace(line);
            if (t.Count == 0) continue;
            string key = t[0];

            if (Eq(key, "OCPROJECT"))
            {
                sawHeader = true;
                if (t.Count > 1) desc.Version = ParseI32(t[1], 1);
            }
            else if (Eq(key, "NAME")) { desc.Name = RestOfLine(line, key.Length); }
            else if (Eq(key, "ENGINE"))
            {
                if (t.Count > 1) desc.EngineName = t[1];
                if (t.Count > 2) desc.EngineMinVersion = t[2];
            }
            else if (Eq(key, "CONTENT")) { if (t.Count > 1) desc.ContentRoot = t[1]; }
            else if (Eq(key, "STARTMAP")) { if (t.Count > 1) desc.StartMap = t[1]; }
            else if (Eq(key, "AUTHOR")) { desc.Author = RestOfLine(line, key.Length); }
            else { desc.UnknownKeys.Add(key); }
        }

        if (!sawHeader)
        {
            error = "not an .ocproject: no OCPROJECT header line";
            return false;
        }
        if (desc.Name.Length == 0)
        {
            error = "missing NAME";
            return false;
        }

        error = null;
        return true;

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // Everything after the key on the cleaned line. NAME and AUTHOR are prose and must not be
        // split on whitespace the way a token row is. Static, taking the span as a parameter,
        // because a ReadOnlySpan cannot be captured by a closure.
        static string RestOfLine(ReadOnlySpan<char> line, int keyLength)
            => Trim(line[keyLength..]).ToString();
    }

    /// <summary>
    /// Loads and validates a manifest against a specific engine version -- the same two gates the
    /// engine applies in <c>loadOcproject</c>.
    /// </summary>
    /// <param name="path">Path to the <c>.ocproject</c> file.</param>
    /// <param name="engineVersion">
    /// The version of the engine build being considered. Pass null to parse without gating, which is
    /// what the launcher's project library wants: it must LIST a project it cannot currently open,
    /// and say why, rather than pretend it is absent.
    /// </param>
    public static bool TryLoad(
        string path,
        string? engineVersion,
        [NotNullWhen(true)] out ProjectDesc? desc,
        [NotNullWhen(false)] out string? error)
    {
        desc = null;

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = $"cannot read file: {path}";
            return false;
        }

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { full = path; }

        var d = new ProjectDesc
        {
            ManifestPath = full,
            Dir = Path.GetDirectoryName(full) ?? string.Empty,
        };

        if (!TryParse(text, d, out error)) return false;

        if (!AverVersion.IsEngineNameMatch(d.EngineName))
        {
            error = $"project targets engine '{d.EngineName}', this is {AverVersion.EngineName}";
            return false;
        }

        if (engineVersion is not null && !AverVersion.Satisfies(d.EngineMinVersion, engineVersion))
        {
            error = $"project needs {AverVersion.EngineName} {d.EngineMinVersion} or newer; " +
                    $"this build is {engineVersion}";
            return false;
        }

        desc = d;
        error = null;
        return true;
    }
}
