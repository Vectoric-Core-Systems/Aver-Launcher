namespace Aver.Launcher.Core;

/// <summary>
/// Version comparison, ported byte-for-byte from the engine's <c>aver::compareVersions</c>
/// (<c>modules/core/include/aver/core/Version.hpp</c>).
/// </summary>
/// <remarks>
/// <para>
/// This is a REIMPLEMENTATION ON PURPOSE, not a convenience wrapper over
/// <see cref="System.Version"/> or a SemVer library, because it has to agree with the engine and
/// those do not:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Missing trailing components read as 0, so <c>"0.1"</c> and <c>"0.1.0"</c> are EQUAL.
///     <c>System.Version</c> treats an absent Build as -1 and orders <c>0.1 &lt; 0.1.0</c>.
///   </description></item>
///   <item><description>
///     Non-numeric junk reads as 0 rather than throwing, because a manifest is untrusted
///     hand-editable text. <c>System.Version.Parse</c> throws; SemVer rejects <c>"0.1"</c> outright.
///   </description></item>
///   <item><description>
///     Accumulation is 32-bit and wraps. Matching that matters less than matching the two above,
///     but a divergence here would be silent, so it is matched too.
///   </description></item>
/// </list>
/// <para>
/// The stake: this comparison is what decides whether an installed engine may open a project, and
/// the engine performs the same check itself at load time (<c>OcProject.cpp</c>). If the launcher
/// and the engine disagree, the launcher offers a project an engine will then refuse — or worse,
/// hides one that would have worked.
/// </para>
/// </remarks>
public static class AverVersion
{
    /// <summary>The engine name a project's <c>ENGINE</c> key must match (<c>kEngineName</c>).</summary>
    public const string EngineName = "Aver";

    /// <summary>
    /// Compares two dotted version strings, returning &lt;0, 0 or &gt;0 like <c>strcmp</c>.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        string sa = a ?? string.Empty;
        string sb = b ?? string.Empty;

        int ia = 0, ib = 0;
        while (ia < sa.Length || ib < sb.Length)
        {
            uint va = 0, vb = 0;

            // Leading run of digits at the current position. If the current character is not a
            // digit the value stays 0 -- that is the "junk reads as 0" rule, not an oversight.
            while (ia < sa.Length && sa[ia] >= '0' && sa[ia] <= '9')
            {
                unchecked { va = (va * 10) + (uint)(sa[ia] - '0'); }
                ia++;
            }
            while (ib < sb.Length && sb[ib] >= '0' && sb[ib] <= '9')
            {
                unchecked { vb = (vb * 10) + (uint)(sb[ib] - '0'); }
                ib++;
            }

            if (va != vb)
            {
                return va < vb ? -1 : 1;
            }

            // Skip whatever followed the digits, then step over the separator. This is what makes
            // "1.2-beta" compare equal to "1.2": the trailing text is walked past, not parsed.
            while (ia < sa.Length && sa[ia] != '.') ia++;
            while (ib < sb.Length && sb[ib] != '.') ib++;
            if (ia < sa.Length) ia++;
            if (ib < sb.Length) ib++;
        }

        return 0;
    }

    /// <summary>
    /// True when an engine build of <paramref name="engineVersion"/> satisfies a project's
    /// <c>ENGINE &lt;name&gt; &lt;minVersion&gt;</c> floor.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>OcProject.cpp</c>: the load fails only when
    /// <c>compareVersions(minVersion, current) &gt; 0</c>. An empty floor satisfies everything.
    /// Note this is a FLOOR, not a pin -- a project asking for 0.1.0 loads under any later build,
    /// which is why per-project version pinning has to live in the launcher's own store.
    /// </remarks>
    public static bool Satisfies(string? engineMinVersion, string? engineVersion)
        => string.IsNullOrEmpty(engineMinVersion) || Compare(engineMinVersion, engineVersion) <= 0;

    /// <summary>
    /// True when a project's <c>ENGINE</c> name names this engine. Empty matches (the key is
    /// optional); comparison is case-insensitive, as <c>equalsCI</c> is.
    /// </summary>
    public static bool IsEngineNameMatch(string? engineName)
        => string.IsNullOrEmpty(engineName)
           || string.Equals(engineName, EngineName, StringComparison.OrdinalIgnoreCase);
}
