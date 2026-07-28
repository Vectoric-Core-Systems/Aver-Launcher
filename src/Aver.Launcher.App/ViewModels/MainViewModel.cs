using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Aver.Launcher.App.Services;
using Aver.Launcher.Core;

namespace Aver.Launcher.App.ViewModels;

public enum Page
{
    Engines,
    Projects,
    System,
    Settings,
}

public sealed class MainViewModel : ObservableObject
{
    private Page _page = Page.Engines;
    private bool _busy;
    private string _status = string.Empty;
    private string? _probeError;
    private PrereqReport? _report;

    public MainViewModel()
    {
        Refresh = new RelayCommand(_ => _ = LoadAsync());
        Launch = new RelayCommand(p => LaunchEngine(p as EngineCardViewModel), p => (p as EngineCardViewModel)?.CanLaunch == true);
        OpenProject = new RelayCommand(p => OpenProjectIn(p as ProjectCardViewModel), p => (p as ProjectCardViewModel)?.CanOpen == true);
        Reveal = new RelayCommand(p => RevealInExplorer(p as string));
        OpenUrl = new RelayCommand(p => Browse(p as string));
        Go = new RelayCommand(p => { if (p is Page pg) CurrentPage = pg; });
    }

    public RelayCommand Refresh { get; }
    public RelayCommand Launch { get; }
    public RelayCommand OpenProject { get; }
    public RelayCommand Reveal { get; }
    public RelayCommand OpenUrl { get; }
    public RelayCommand Go { get; }

    public ObservableCollection<EngineCardViewModel> Engines { get; } = [];
    public ObservableCollection<ProjectCardViewModel> Projects { get; } = [];
    public ObservableCollection<PrereqRowViewModel> Prereqs { get; } = [];

    public Page CurrentPage
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            Raise(nameof(IsEngines));
            Raise(nameof(IsProjects));
            Raise(nameof(IsSystem));
            Raise(nameof(IsSettings));
            Raise(nameof(PageTitle));
            Raise(nameof(PageSubtitle));
        }
    }

    public bool IsEngines => CurrentPage == Page.Engines;
    public bool IsProjects => CurrentPage == Page.Projects;
    public bool IsSystem => CurrentPage == Page.System;
    public bool IsSettings => CurrentPage == Page.Settings;

    public string PageTitle => CurrentPage switch
    {
        Page.Engines => "Engine Versions",
        Page.Projects => "Projects",
        Page.System => "System",
        _ => "Settings",
    };

    public string PageSubtitle => CurrentPage switch
    {
        Page.Engines => "Installed editions of Aver Engine on this machine.",
        Page.Projects => "Projects found in your recent list and projects folder.",
        Page.System => "What this machine can run, and what it would give up.",
        _ => "Where the launcher keeps things.",
    };

    public bool Busy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string? ProbeError
    {
        get => _probeError;
        private set { Set(ref _probeError, value); Raise(nameof(HasProbeError)); }
    }

    public bool HasProbeError => ProbeError is not null;

    // ---- headline verdict, shown on the System page ----

    public bool HasReport => _report is not null;

    public bool CanRunEngine => _report?.CanRunEngine ?? false;

    public string VerdictTitle => _report is null
        ? "Checking this machine..."
        : _report.CanRunEngine ? "This machine can run Aver Engine" : "This machine cannot run Aver Engine";

    public string VerdictDetail
    {
        get
        {
            if (_report is null) return string.Empty;
            int blocking = Prereqs.Count(p => p.Prereq.Severity == PrereqSeverity.Blocking);
            int limited = Prereqs.Count(p => p.Prereq.Severity == PrereqSeverity.Degraded);
            if (blocking > 0) return $"{blocking} blocking requirement{(blocking == 1 ? string.Empty : "s")} unmet.";
            return limited == 0
                ? "Every checked requirement is satisfied."
                : $"{limited} feature{(limited == 1 ? " is" : "s are")} unavailable or reduced.";
        }
    }

    public string AdapterText => _report?.Caps.AdapterName ?? string.Empty;

    public string OsText => _report is null
        ? string.Empty
        : $"{_report.Os.Family} build {_report.Os.Build}.{_report.Os.UpdateBuildRevision} {_report.Os.DisplayVersion}".TrimEnd();

    // ---- paths, shown on Settings ----

    public string InstallRoot => EngineInstallStore.DefaultRoot;
    public string ProjectsRoot => RecentProjects.DefaultProjectsRoot;
    public string RecentsFile => RecentProjects.RecentsPath;

    public string EnginesEmptyText =>
        $"No engine installs found under\n{InstallRoot}\n\n"
        + "Downloading is not built yet. Stage a build there with the engine's\n"
        + "scripts/stage-payload.ps1 and press Refresh.";

    public async Task LoadAsync()
    {
        Busy = true;
        Status = "Scanning installs...";
        try
        {
            IReadOnlyList<EngineInstall> installs = await Task.Run(() => EngineInstallStore.Scan()).ConfigureAwait(true);
            Engines.Clear();
            foreach (EngineInstall i in installs) Engines.Add(new EngineCardViewModel(i));

            Status = "Reading projects...";
            IReadOnlyList<ProjectEntry> projects = await Task.Run(() => ProjectLibrary.Load()).ConfigureAwait(true);
            Projects.Clear();
            foreach (ProjectEntry p in projects) Projects.Add(new ProjectCardViewModel(p, installs));

            Status = "Probing hardware...";
            ProbeRunner.Result probe = await ProbeRunner.RunAsync().ConfigureAwait(true);
            _report = probe.Report;
            ProbeError = probe.Error;

            Prereqs.Clear();
            if (_report is not null)
            {
                foreach (Prereq p in _report.Evaluate()) Prereqs.Add(new PrereqRowViewModel(p));
            }

            Raise(nameof(HasReport));
            Raise(nameof(CanRunEngine));
            Raise(nameof(VerdictTitle));
            Raise(nameof(VerdictDetail));
            Raise(nameof(AdapterText));
            Raise(nameof(OsText));

            Status = $"{Engines.Count} engine install{(Engines.Count == 1 ? string.Empty : "s")}, "
                     + $"{Projects.Count} project{(Projects.Count == 1 ? string.Empty : "s")}";
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// Starts the editor. No project argument, so the editor shows its own start screen.
    /// </summary>
    /// <remarks>
    /// The working directory is set to the install root even though the editor resolves its assets from
    /// <c>executableDir()</c> and never from the working directory. That is deliberate belt-and-braces:
    /// anything the editor later shells out to inherits a sane directory rather than wherever the
    /// launcher happened to be started from.
    /// </remarks>
    private void LaunchEngine(EngineCardViewModel? card)
    {
        if (card is null || !card.CanLaunch) return;
        Start(card.Install.EntryPoint, card.Install.Root, []);
    }

    /// <summary>
    /// Opens a project by passing its manifest path positionally, which is how the editor takes one.
    /// </summary>
    /// <remarks>
    /// A positional <c>.ocproject</c> also suppresses the editor's own start screen, so the launcher and
    /// the browser do not both try to be the chooser.
    /// </remarks>
    private void OpenProjectIn(ProjectCardViewModel? card)
    {
        if (card?.Engine is null || card.Entry.Desc is null) return;
        Start(card.Engine.EntryPoint, card.Engine.Root, [card.Entry.ManifestPath]);
    }

    private void Start(string exe, string workingDir, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Directory.Exists(workingDir) ? workingDir : Path.GetDirectoryName(exe) ?? string.Empty,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            Status = $"Started {Path.GetFileName(exe)}";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Status = $"Could not start {Path.GetFileName(exe)}: {ex.Message}";
        }
    }

    private void RevealInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            // /select, wants a file; a directory opens directly.
            bool isFile = File.Exists(path);
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            if (isFile) psi.Arguments = $"/select,\"{path}\"";
            else psi.Arguments = $"\"{path}\"";
            Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = $"Could not open Explorer: {ex.Message}";
        }
    }

    private void Browse(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttps) return;   // only ever the fix-it links this app authored
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = $"Could not open the browser: {ex.Message}";
        }
    }
}
