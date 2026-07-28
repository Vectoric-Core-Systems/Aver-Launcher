using System.Globalization;
using System.IO;
using Aver.Launcher.Core;

namespace Aver.Launcher.App.ViewModels;

/// <summary>One installed engine version, as an "engine slot" card.</summary>
public sealed class EngineCardViewModel(EngineInstall install)
{
    public EngineInstall Install { get; } = install;

    public string Version => Install.Version;

    public string EditionName => Install.Edition.Id.ToUpperInvariant();

    public string SizeText => $"{Install.SizeBytes / (1024.0 * 1024.0):F1} MB";

    public string Root => Install.Root;

    public string InstalledText => Install.InstalledUtc == DateTime.MinValue
        ? "unknown"
        : Install.InstalledUtc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>Optional modules present, as chips.</summary>
    public IReadOnlyList<string> Included => Install.Edition.IncludedModules;

    /// <summary>Optional modules absent, as dimmed chips with the cost in the tooltip.</summary>
    public IReadOnlyList<ChipViewModel> Excluded => Install.Edition.ExcludedModules
        .Select(x => new ChipViewModel(x.Module, x.Consequence))
        .ToList();

    public string BackendText => string.Join(" / ", Install.Edition.Backends);

    public bool CanLaunch => Install.IsUsable && Install.Edition.CanEditProjects;

    /// <summary>Set when the install is present but cannot be launched, so the card can say why.</summary>
    public string? Problem
    {
        get
        {
            if (!Install.IsUsable) return $"{Path.GetFileName(Install.EntryPoint)} is missing from this install.";
            if (!Install.Edition.CanEditProjects)
            {
                return Install.Edition.IsOn(EngineOptions.EnableUi)
                    ? "Built without Direct3D 12, so it has no working render backend."
                    : "Built without the editor UI, so it cannot open projects.";
            }
            if (!Install.Edition.IsComplete(out IReadOnlyList<string> missing))
            {
                return "This build predates " + string.Join(", ", missing)
                       + ", so what it contains cannot be stated.";
            }
            return null;
        }
    }

    public string ProvenanceText
    {
        get
        {
            string commit = Install.Payload?.SourceCommit ?? string.Empty;
            string shortSha = commit.Length >= 7 ? commit[..7] : commit;
            string dirty = Install.Payload?.SourceDirty == true ? " (dirty)" : string.Empty;
            return shortSha.Length == 0 ? "unknown source" : $"{shortSha}{dirty}";
        }
    }
}

/// <summary>A module chip with a hover explanation.</summary>
public sealed record ChipViewModel(string Text, string? Tooltip);

/// <summary>One (edition, version) the feed offers, as a dropdown entry.</summary>
public sealed class SlotOption(string edition, string version, FeedEdition feed)
{
    public string Edition { get; } = edition;

    public string Version { get; } = version;

    public FeedEdition Feed { get; } = feed;

    /// <summary>What the dropdown shows: "0.1.0 - standard".</summary>
    public string Label => $"{Version}  -  {Edition}";

    /// <summary>Modules this edition advertises, from the index rather than from a download.</summary>
    public IReadOnlyList<string> Modules => EngineOptions.All
        .Where(o => o.Kind == EngineOptionKind.Module
                    && Feed.Options.TryGetValue(o.Key, out bool on) && on)
        .Select(o => o.DisplayName)
        .ToList();
}

/// <summary>
/// An engine SLOT: a place for a version you have not installed yet.
/// </summary>
/// <remarks>
/// Modelled on the Unreal Engine tab in Epic's launcher, where the plus button adds a slot rather
/// than installing something immediately. A slot starts on the newest version and carries a dropdown
/// to pick an older one, so choosing a version is one control on the card rather than a wall of cards
/// -- which matters here more than it does for Unreal, because Aver multiplies versions by editions
/// and a card per combination would grow quadratically.
/// </remarks>
public sealed class EngineSlotViewModel : ObservableObject
{
    private double _progress;
    private string _status = string.Empty;
    private bool _busy;
    private SlotOption _selected;

    public EngineSlotViewModel(IReadOnlyList<SlotOption> options, SlotOption? initial = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _selected = initial ?? options[0];
    }

    /// <summary>Everything the feed offers that is not already installed.</summary>
    public IReadOnlyList<SlotOption> Options { get; }

    /// <summary>The version this slot will install. Defaults to the newest.</summary>
    public SlotOption Selected
    {
        get => _selected;
        set
        {
            // The ComboBox can hand back null while its items are being rebuilt.
            if (value is null || !Set(ref _selected, value)) return;
            Raise(nameof(Version));
            Raise(nameof(EditionName));
            Raise(nameof(Modules));
        }
    }

    public string Edition => Selected.Edition;

    public string Version => Selected.Version;

    public string EditionName => Selected.Edition.ToUpperInvariant();

    public IReadOnlyList<string> Modules => Selected.Modules;

    /// <summary>A slot with one choice hides its dropdown; a caret that cannot open is noise.</summary>
    public bool HasChoice => Options.Count > 1;

    public bool Busy
    {
        get => _busy;
        set { if (Set(ref _busy, value)) Raise(nameof(NotBusy)); }
    }

    public bool NotBusy => !_busy;

    /// <summary>0..1 for the progress bar.</summary>
    public double Progress
    {
        get => _progress;
        set { if (Set(ref _progress, value)) Raise(nameof(ProgressPercent)); }
    }

    public double ProgressPercent => _progress * 100;

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }
}

/// <summary>One project in the library.</summary>
public sealed class ProjectCardViewModel(ProjectEntry entry, IReadOnlyList<EngineInstall> installs)
{
    public ProjectEntry Entry { get; } = entry;

    public string Name => Entry.DisplayName;

    public string Directory => Entry.Directory;

    public bool IsRecent => Entry.IsRecent;

    public string EngineText => Entry.EngineMinVersion.Length == 0
        ? "any engine"
        : $"needs Aver {Entry.EngineMinVersion} or newer";

    public string StartMapText => Entry.Desc?.StartMap is { Length: > 0 } m ? m : "no start map";

    private readonly EngineChoice _choice = entry.Resolve(installs);

    /// <summary>The install that will actually be used, or null when none qualifies.</summary>
    public EngineInstall? Engine => _choice.Install;

    public bool CanOpen => Engine is not null && Entry.Desc is not null;

    /// <summary>True when the chosen engine has everything the project needs.</summary>
    public bool FullySatisfied => _choice.FullySatisfied;

    /// <summary>True when an engine was found but it is missing something the project uses.</summary>
    public bool HasShortfall => Engine is not null && _choice.Missing.Count > 0;

    public string OpenWithText => Engine is null ? "no installed engine qualifies" : $"opens with {Engine.Label}";

    /// <summary>
    /// What the project needs, as chips, so the requirement is visible before anything goes wrong.
    /// </summary>
    public IReadOnlyList<ChipViewModel> Requires => Entry.Requirements.Evidence
        .OrderBy(e => e.ModuleName, StringComparer.OrdinalIgnoreCase)
        .Select(e => new ChipViewModel(e.ModuleName, e.Reason))
        .ToList();

    /// <summary>
    /// The shortfall, named with its consequence.
    /// </summary>
    /// <remarks>
    /// This is the message that would have saved the confusion: an edition without physics opens
    /// SkyForge, renders the arena correctly, and then does nothing on Play. Nothing errors, so
    /// without saying it here the user is left to work it out from a level that looks right.
    /// </remarks>
    public string? Shortfall
    {
        get
        {
            if (!HasShortfall) return null;
            RequirementEvidence[] m = [.. _choice.Missing];
            string names = string.Join(", ", m.Select(x => x.ModuleName));
            string consequences = string.Join(" ",
                m.Select(x => EngineOptions.Find(x.Module)?.Consequence).Where(c => c is not null));
            return $"{Engine!.Edition.Id} is built without {names}. {consequences} "
                   + $"({m[0].Reason})";
        }
    }

    /// <summary>Set when the project cannot be opened at all.</summary>
    public string? Problem
    {
        get
        {
            if (Entry.Error is not null) return Entry.Error;
            if (Engine is null)
            {
                return Entry.EngineMinVersion.Length > 0
                    ? $"No installed edition satisfies Aver {Entry.EngineMinVersion}."
                    : "No installed edition can open projects.";
            }
            return null;
        }
    }
}

/// <summary>One prerequisite row.</summary>
public sealed class PrereqRowViewModel(Prereq prereq)
{
    public Prereq Prereq { get; } = prereq;

    public string Name => Prereq.Name;

    public string Detail => Prereq.Detail;

    public string? Consequence => Prereq.Consequence;

    public string? FixUrl => Prereq.FixUrl;

    public bool HasFix => Prereq.FixUrl is not null;

    public bool HasConsequence => Prereq.Consequence is not null;

    public string StatusText => Prereq.Severity switch
    {
        PrereqSeverity.Ok => "OK",
        PrereqSeverity.Degraded => "LIMITED",
        _ => "BLOCKED",
    };

    /// <summary>Resource key of the brush for this severity, resolved in XAML.</summary>
    public string StatusBrushKey => Prereq.Severity switch
    {
        PrereqSeverity.Ok => "Ok",
        PrereqSeverity.Degraded => "Warn",
        _ => "Fail",
    };
}
