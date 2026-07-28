namespace Aver.Launcher.Core;

/// <summary>How well an edition answers a set of wanted modules.</summary>
public enum MatchKind
{
    /// <summary>Exactly the modules asked for.</summary>
    Exact,

    /// <summary>Everything asked for, plus more. Works, but larger than requested.</summary>
    Superset,

    /// <summary>Missing something that was asked for.</summary>
    Short,
}

/// <summary>An edition offered against a wanted module set.</summary>
/// <param name="Edition">Feed edition id.</param>
/// <param name="Version">Version.</param>
/// <param name="Kind">How well it matches.</param>
/// <param name="Missing">Wanted modules this edition does not have.</param>
/// <param name="Extra">Modules it has that were not asked for.</param>
public sealed record EditionMatch(
    string Edition,
    string Version,
    MatchKind Kind,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Extra)
{
    public bool Usable => Kind != MatchKind.Short;
}

/// <summary>
/// Maps a wanted set of modules onto the editions that actually exist.
/// </summary>
/// <remarks>
/// <para>
/// Modules are compile-time CMake options built as static libraries, so the launcher cannot compose
/// one on demand -- it can only pick among builds someone published. A UI that presented free
/// checkboxes would be lying about that.
/// </para>
/// <para>
/// So checkboxes are treated as a QUERY, not a specification: the user says what they want, and this
/// answers with the edition that best provides it, naming anything extra it drags along or anything
/// it cannot supply. That keeps the familiar interaction while staying truthful about what is on
/// offer.
/// </para>
/// </remarks>
public static class EditionMatcher
{
    /// <summary>Optional modules a user can meaningfully ask for.</summary>
    public static IReadOnlyList<EngineOption> Choosable { get; } =
        EngineOptions.All.Where(o => o.Kind == EngineOptionKind.Module).ToList();

    /// <summary>Ranks candidate editions against a wanted module set, best first.</summary>
    public static IReadOnlyList<EditionMatch> Rank(
        IReadOnlySet<string> wanted,
        IEnumerable<(string Edition, string Version, IReadOnlyDictionary<string, bool> Options)> candidates)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(candidates);

        var results = new List<EditionMatch>();
        foreach ((string edition, string version, IReadOnlyDictionary<string, bool> options) in candidates)
        {
            var has = Choosable
                .Where(o => options.TryGetValue(o.Key, out bool on) && on)
                .Select(o => o.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<string> missing = [.. wanted.Where(w => !has.Contains(w))];
            List<string> extra = [.. has.Where(h => !wanted.Contains(h))];

            MatchKind kind = missing.Count > 0
                ? MatchKind.Short
                : extra.Count == 0 ? MatchKind.Exact : MatchKind.Superset;

            results.Add(new EditionMatch(edition, version, kind, missing, extra));
        }

        return results
            .OrderBy(r => r.Kind)                     // Exact, then Superset, then Short
            .ThenBy(r => r.Extra.Count)               // among supersets, the leanest that still fits
            .ThenByDescending(r => r.Version, Comparer<string>.Create(AverVersion.Compare))
            .ThenBy(r => r.Edition, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>A sentence describing the best match, for the dialog to show.</summary>
    public static string Describe(EditionMatch? best)
    {
        if (best is null) return "No edition is available.";

        return best.Kind switch
        {
            MatchKind.Exact =>
                $"The {best.Edition} edition matches exactly.",
            MatchKind.Superset =>
                $"The closest is the {best.Edition} edition, which also includes "
                + $"{Join(best.Extra)}. Modules are chosen when the engine is built, so an exact "
                + "combination only exists if someone published it.",
            _ =>
                $"No published edition has {Join(best.Missing)}. The closest is {best.Edition}.",
        };

        static string Join(IReadOnlyList<string> keys)
        {
            string[] names = [.. keys.Select(k => EngineOptions.Find(k)?.DisplayName ?? k)];
            return names.Length switch
            {
                0 => "nothing",
                1 => names[0],
                _ => string.Join(", ", names[..^1]) + " and " + names[^1],
            };
        }
    }
}
