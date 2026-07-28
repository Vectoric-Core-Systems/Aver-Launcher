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

    /// <summary>The install that will actually be used, or null when none qualifies.</summary>
    public EngineInstall? Engine { get; } = entry.BestEngine(installs);

    public bool CanOpen => Engine is not null && Entry.Desc is not null;

    public string OpenWithText => Engine is null ? "no installed engine qualifies" : $"opens with {Engine.Label}";

    /// <summary>Set when the project cannot be opened, explaining which of the two reasons applies.</summary>
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
