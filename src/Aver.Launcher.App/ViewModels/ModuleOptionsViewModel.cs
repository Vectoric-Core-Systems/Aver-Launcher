using System.Collections.ObjectModel;
using Aver.Launcher.Core;

namespace Aver.Launcher.App.ViewModels;

/// <summary>One module checkbox in the options dialog.</summary>
public sealed class ModuleChoice(EngineOption option, bool on, Action changed) : ObservableObject
{
    private bool _on = on;

    public string Key => option.Key;

    public string Name => option.DisplayName;

    public string Description => option.Description;

    /// <summary>What turning this off costs, shown under the checkbox.</summary>
    public string Consequence => option.Consequence;

    public bool IsOn
    {
        get => _on;
        set { if (Set(ref _on, value)) changed(); }
    }

    /// <summary>True when no published edition offers this module at all.</summary>
    public bool Unavailable { get; init; }
}

/// <summary>
/// The install-options dialog: pick modules, get told which edition that resolves to.
/// </summary>
/// <remarks>
/// The checkboxes are a QUERY, not a build specification. Modules are compile-time options, so the
/// launcher can only choose among editions someone published; the dialog says so plainly rather than
/// implying it can assemble any combination. This is the one place a familiar-looking UI could
/// mislead, so the resolved answer is always on screen next to the choices.
/// </remarks>
public sealed class ModuleOptionsViewModel : ObservableObject
{
    private readonly List<(string Edition, string Version, IReadOnlyDictionary<string, bool> Options)> _candidates;
    private string _resolution = string.Empty;
    private EditionMatch? _best;

    public ModuleOptionsViewModel(
        string title,
        IEnumerable<(string Edition, string Version, IReadOnlyDictionary<string, bool> Options)> candidates,
        IReadOnlyDictionary<string, bool>? initial = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        Title = title;
        _candidates = [.. candidates];

        // A module no published edition has is shown, but disabled: its absence is a fact about what
        // exists, and hiding it would leave the user wondering where it went.
        var everAvailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((_, _, IReadOnlyDictionary<string, bool> o) in _candidates)
        {
            foreach (EngineOption m in EditionMatcher.Choosable)
            {
                if (o.TryGetValue(m.Key, out bool on) && on) everAvailable.Add(m.Key);
            }
        }

        foreach (EngineOption m in EditionMatcher.Choosable)
        {
            bool on = initial is not null && initial.TryGetValue(m.Key, out bool v) && v;
            Modules.Add(new ModuleChoice(m, on && everAvailable.Contains(m.Key), Resolve)
            {
                Unavailable = !everAvailable.Contains(m.Key),
            });
        }

        Resolve();
    }

    public string Title { get; }

    public ObservableCollection<ModuleChoice> Modules { get; } = [];

    /// <summary>Plain-language account of which edition the current choice resolves to.</summary>
    public string Resolution
    {
        get => _resolution;
        private set => Set(ref _resolution, value);
    }

    public bool CanProceed => _best is { Usable: true };

    /// <summary>The edition the user will actually get.</summary>
    public EditionMatch? Best => _best;

    public string ProceedLabel => _best is null ? "INSTALL" : $"INSTALL {_best.Edition} {_best.Version}";

    private void Resolve()
    {
        var wanted = Modules.Where(m => m.IsOn).Select(m => m.Key)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<EditionMatch> ranked = EditionMatcher.Rank(wanted, _candidates);
        _best = ranked.Count > 0 ? ranked[0] : null;

        Resolution = EditionMatcher.Describe(_best);
        Raise(nameof(CanProceed));
        Raise(nameof(Best));
        Raise(nameof(ProceedLabel));
    }
}
