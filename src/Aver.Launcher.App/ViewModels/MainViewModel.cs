using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Aver.Launcher.App.Services;
using Aver.Launcher.Core;
using Aver.Launcher.Platform;
using Aver.Launcher.Updater;

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
        InstallVersion = new RelayCommand(
            p => _ = InstallWithOptionsAsync(p as EngineSlotViewModel),
            p => (p as EngineSlotViewModel)?.NotBusy == true);
        ModifyInstall = new RelayCommand(p => _ = ModifyInstallAsync(p as EngineCardViewModel));
        VerifyFiles = new RelayCommand(p => VerifyInstall(p as EngineCardViewModel));
        Uninstall = new RelayCommand(p => _ = UninstallAsync(p as EngineCardViewModel));
        AddSlot = new RelayCommand(_ => AddEngineSlot(), _ => CanAddSlot);
        RemoveSlot = new RelayCommand(
            p => { if (p is EngineSlotViewModel s && s.NotBusy) Slots.Remove(s); },
            p => (p as EngineSlotViewModel)?.NotBusy == true);
    }

    public RelayCommand InstallVersion { get; }

    /// <summary>The Options menu beside LAUNCH, on an installed engine.</summary>
    public RelayCommand ModifyInstall { get; }

    public RelayCommand VerifyFiles { get; }

    public RelayCommand Uninstall { get; }

    /// <summary>Adds an empty slot, on the newest version. The plus button.</summary>
    public RelayCommand AddSlot { get; }

    public RelayCommand RemoveSlot { get; }

    /// <summary>Slots the user has added but not installed yet.</summary>
    public ObservableCollection<EngineSlotViewModel> Slots { get; } = [];

    /// <summary>Everything the feed offers that is not already installed.</summary>
    private IReadOnlyList<SlotOption> _options = [];

    public bool CanAddSlot => _options.Count > 0;

    /// <summary>
    /// Why the plus button is or is not usable. A disabled control with no explanation is the thing
    /// people file bugs about.
    /// </summary>
    public string AddSlotTip => _options.Count > 0
        ? $"Add an engine version ({_options.Count} available)"
        : "Nothing to add: the feed offers no versions that are not already installed";

    /// <summary>
    /// Adds a slot preselected to the newest version, which is what the plus button should do:
    /// the common case is "give me the current engine", and picking an older one is the exception
    /// the dropdown exists for.
    /// </summary>
    private void AddEngineSlot()
    {
        if (_options.Count == 0) return;
        Slots.Add(new EngineSlotViewModel(_options, Newest(_options)));
        Raise(nameof(HasSlots));
    }

    /// <summary>Newest by the engine's own version ordering, breaking ties toward the fuller edition.</summary>
    private static SlotOption Newest(IReadOnlyList<SlotOption> options)
        => options
            .OrderByDescending(o => o.Version, Comparer<string>.Create(AverVersion.Compare))
            .ThenByDescending(o => o.Modules.Count)
            .ThenBy(o => o.Edition, StringComparer.OrdinalIgnoreCase)
            .First();

    public bool HasSlots => Slots.Count > 0;

    private readonly LauncherSettings _settings = LauncherSettings.Load();

    public string FeedUrl => _settings.FeedUrl;

    private string? _feedError;
    private string _feedNote = string.Empty;
    private bool _nothingAvailable;

    /// <summary>Set only when the feed could not be reached or read. Not for "nothing published".</summary>
    public string? FeedError
    {
        get => _feedError;
        private set { Set(ref _feedError, value); Raise(nameof(HasFeedError)); }
    }

    public bool HasFeedError => _feedError is not null;

    /// <summary>
    /// True when the feed answered but has nothing to offer -- either nothing published, or
    /// everything it lists is already installed.
    /// </summary>
    public bool NothingAvailable
    {
        get => _nothingAvailable;
        private set => Set(ref _nothingAvailable, value);
    }

    /// <summary>The line shown in that case.</summary>
    public string FeedNote
    {
        get => _feedNote;
        private set => Set(ref _feedNote, value);
    }

    /// <summary>Reads the feed and lists versions that are not installed.</summary>
    /// <remarks>
    /// "Nothing published yet" is deliberately NOT an error. A repository that exists with no
    /// releases is the normal state of a project that has not shipped, and dressing a 404 up as a
    /// failure would make a healthy setup look broken.
    /// </remarks>
    private async Task LoadFeedAsync(IReadOnlyList<EngineInstall> installed)
    {
        _options = [];
        FeedError = null;
        FeedNote = string.Empty;
        NothingAvailable = false;

        FeedSource source = FeedSource.Open(_settings.FeedUrl);
        FeedResult result = await source.GetIndexAsync().ConfigureAwait(true);

        switch (result.Status)
        {
            case FeedStatus.Unreachable:
                FeedError = result.Message;
                return;

            case FeedStatus.NoReleases:
                NothingAvailable = true;
                FeedNote = "Nothing available to install. "
                           + (result.Message ?? "No releases have been published yet.");
                return;

            case FeedStatus.NotModified:
                return;

            default:
                break;
        }

        var have = installed
            .Select(i => $"{i.Edition.Id}/{i.Version}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var options = new List<SlotOption>();
        foreach ((string edition, FeedEdition fe) in result.Index!.Editions)
        {
            foreach (string v in fe.Versions)
            {
                if (have.Contains($"{edition}/{v}")) continue;
                options.Add(new SlotOption(edition, v, fe));
            }
        }

        _options = options
            .OrderByDescending(o => o.Version, Comparer<string>.Create(AverVersion.Compare))
            .ThenBy(o => o.Edition, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Drop slots whose choice has just been installed, and re-seat any whose selection is gone.
        for (int i = Slots.Count - 1; i >= 0; i--)
        {
            EngineSlotViewModel slot = Slots[i];
            if (slot.Busy) continue;
            if (have.Contains($"{slot.Edition}/{slot.Version}")) Slots.RemoveAt(i);
        }
        Raise(nameof(HasSlots));
        Raise(nameof(CanAddSlot));
        Raise(nameof(AddSlotTip));

        if (_options.Count == 0)
        {
            NothingAvailable = true;
            FeedNote = "Nothing available to install. Every version the feed offers is already here.";
        }
    }

    /// <summary>
    /// Opens the options dialog for a slot, then installs whatever it resolved to.
    /// </summary>
    /// <remarks>
    /// The dialog starts from the modules of the slot's currently selected edition, so the common
    /// path is "open, glance, install" rather than "reconstruct my intent from nothing".
    /// </remarks>
    private async Task InstallWithOptionsAsync(EngineSlotViewModel? slot)
    {
        if (slot is null || slot.Busy) return;

        var candidates = slot.Options
            .Select(o => (o.Edition, o.Version, (IReadOnlyDictionary<string, bool>)o.Feed.Options))
            .ToList();

        var vm = new ModuleOptionsViewModel(
            $"Install Aver Engine {slot.Version}", candidates, slot.Selected.Feed.Options);

        var dlg = new ModuleOptionsWindow(vm) { Owner = Application.Current?.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Chosen is null) return;

        // Re-seat the slot on whatever the dialog resolved to, so the card and the install agree.
        SlotOption? resolved = slot.Options.FirstOrDefault(
            o => string.Equals(o.Edition, dlg.Chosen.Edition, StringComparison.OrdinalIgnoreCase)
                 && o.Version == dlg.Chosen.Version);
        if (resolved is not null) slot.Selected = resolved;

        await InstallAsync(slot).ConfigureAwait(true);
    }

    /// <summary>Options for an INSTALLED engine: change modules, verify, or remove.</summary>
    private async Task ModifyInstallAsync(EngineCardViewModel? card)
    {
        if (card is null) return;

        var candidates = _options
            .Select(o => (o.Edition, o.Version, (IReadOnlyDictionary<string, bool>)o.Feed.Options))
            .ToList();

        if (candidates.Count == 0)
        {
            Status = "Nothing else is available to switch to: the feed offers no other edition.";
            return;
        }

        var vm = new ModuleOptionsViewModel(
            $"Modify {card.Install.Label}", candidates, card.Install.Edition.Options);

        var dlg = new ModuleOptionsWindow(vm) { Owner = Application.Current?.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Chosen is null) return;

        SlotOption? resolved = _options.FirstOrDefault(
            o => string.Equals(o.Edition, dlg.Chosen.Edition, StringComparison.OrdinalIgnoreCase)
                 && o.Version == dlg.Chosen.Version);
        if (resolved is null)
        {
            Status = $"{dlg.Chosen.Edition} {dlg.Chosen.Version} is already installed.";
            return;
        }

        // Changing modules means installing a different EDITION beside this one, not mutating it in
        // place: a build cannot gain a module it was not compiled with. Both remain usable.
        var slot = new EngineSlotViewModel(_options, resolved);
        Slots.Add(slot);
        Raise(nameof(HasSlots));
        await InstallAsync(slot).ConfigureAwait(true);
    }

    private void VerifyInstall(EngineCardViewModel? card)
    {
        if (card is null) return;
        Status = $"Verifying {card.Install.Label}...";
        VerifyReport report = InstallMaintenance.Verify(card.Install);
        Status = $"{card.Install.Label}: {report.Summary}";
    }

    private async Task UninstallAsync(EngineCardViewModel? card)
    {
        if (card is null) return;

        MessageBoxResult confirm = MessageBox.Show(
            $"Remove {card.Install.Label}?\n\n{card.Install.Root}\n\n"
            + "Projects and their content are not touched.",
            "Remove engine", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            InstallMaintenance.Uninstall(card.Install, new WindowsFileSystemOps());
            Status = $"Removed {card.Install.Label}";
            await LoadAsync().ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            Status = ex.Message;
        }
    }

    private async Task InstallAsync(EngineSlotViewModel? card)
    {
        if (card is null || card.Busy) return;
        card.Busy = true;
        card.Status = "starting";
        try
        {
            FeedSource source = FeedSource.Open(_settings.FeedUrl);
            FeedResult feed = await source.GetIndexAsync().ConfigureAwait(true);
            if (feed.Index is null)
            {
                card.Status = feed.Message ?? "feed unavailable";
                return;
            }

            VersionManifest manifest = await source.GetManifestAsync(feed.Index, card.Edition, card.Version)
                                                   .ConfigureAwait(true);

            string root = _settings.EffectiveInstallRoot;
            Dictionary<string, string> inventory =
                PackInstaller.BuildLocalInventory(EngineInstallStore.Scan(root));

            (IRangeFetcher fetcher, Func<long> transferred) = source.OpenPack(manifest);
            PackIndex packIndex = await FeedClient.GetPackIndexAsync(fetcher).ConfigureAwait(true);

            var progress = new Progress<InstallProgress>(p =>
            {
                card.Progress = p.Fraction;
                card.Status = p.Phase;
            });

            var installer = new PackInstaller(new WindowsFileSystemOps());
            InstallReport report = await installer
                .InstallAsync(manifest, packIndex, root, inventory, fetcher, progress)
                .ConfigureAwait(true);

            Status = $"Installed {card.Edition} {card.Version}: "
                     + $"{report.FilesReused} file(s) reused, {transferred() / 1048576.0:F2} MB downloaded";
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                       or NotSupportedException or TaskCanceledException)
        {
            card.Status = ex.Message;
            Status = $"Install failed: {ex.Message}";
        }
        finally
        {
            card.Busy = false;
        }
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

    public string InstallRoot => _settings.EffectiveInstallRoot;
    public string ProjectsRoot => RecentProjects.DefaultProjectsRoot;
    public string RecentsFile => RecentProjects.RecentsPath;

    public string EnginesEmptyText =>
        $"No engine installs found under\n{InstallRoot}\n\n"
        + "Use + above to add a version from the feed, or stage a build there with the\n"
        + "engine's scripts/stage-payload.ps1 and press Refresh.";

    public async Task LoadAsync()
    {
        Busy = true;
        Status = "Scanning installs...";
        try
        {
            IReadOnlyList<EngineInstall> installs = await Task.Run(() => EngineInstallStore.Scan(_settings.EffectiveInstallRoot)).ConfigureAwait(true);
            Engines.Clear();
            foreach (EngineInstall i in installs) Engines.Add(new EngineCardViewModel(i));

            Status = "Reading the feed...";
            await LoadFeedAsync(installs).ConfigureAwait(true);

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



