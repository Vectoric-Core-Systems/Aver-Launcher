using System.Windows;
using Aver.Launcher.App.Services;
using Aver.Launcher.Core;
using Aver.Launcher.Platform;
using Aver.Launcher.Updater;

namespace Aver.Launcher.App;

public partial class App : Application
{
    /// <summary>
    /// Entry point. Handles the probe mode before WPF is touched, then starts the UI.
    /// </summary>
    /// <remarks>
    /// <c>AverLauncher.exe --probe --json</c> is how the UI asks for hardware capabilities: it re-execs
    /// this same binary and reads JSON back, because probing creates a real D3D12 device and therefore
    /// runs vendor driver code. A fault there costs a child process and an error message rather than the
    /// whole launcher. Returning before <see cref="Application"/> is constructed keeps that path free of
    /// any WPF startup cost.
    /// </remarks>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains(ProbeRunner.ProbeArgument, StringComparer.Ordinal))
        {
            return RunProbe(args.Contains(ProbeRunner.JsonArgument, StringComparer.Ordinal));
        }

        var app = new App();
        app.InitializeComponent();

        // Diagnostic, in the same spirit as --probe: opens the installation-options dialog against
        // the configured feed and exits. A modal dialog is otherwise only reachable by clicking, and
        // "does it render at all" is worth being able to answer without a mouse.
        if (args.Contains("--options-preview", StringComparer.Ordinal))
        {
            return ShowOptionsPreview();
        }

        return app.Run(new MainWindow());
    }

    private static int RunProbe(bool json)
    {
        // A WinExe has no console of its own. When the parent redirects stdout this writes into the
        // pipe as normal; when a human runs it from a terminal, attaching to the parent console is what
        // makes the output visible instead of vanishing.
        if (!json) ConsoleAttach.ToParent();

        PrereqReport report = SystemProbe.QueryAll(out string? warning);

        if (json)
        {
            Console.Out.Write(ProbeRunner.Serialise(report));
            Console.Out.Flush();
        }
        else
        {
            if (warning is not null) Console.Error.WriteLine($"! {warning}");
            foreach (Prereq p in report.Evaluate())
            {
                Console.WriteLine($"[{p.Severity,-8}] {p.Name,-28} {p.Detail}");
            }
        }

        return report.CanRunEngine ? 0 : 1;
    }

    private static int ShowOptionsPreview()
    {
        LauncherSettings settings = LauncherSettings.Load();
        FeedSource source = FeedSource.Open(settings.FeedUrl);
        FeedResult feed = source.GetIndexAsync().GetAwaiter().GetResult();

        var candidates = new List<(string, string, IReadOnlyDictionary<string, bool>)>();
        if (feed.Index is not null)
        {
            foreach ((string edition, FeedEdition fe) in feed.Index.Editions)
            {
                foreach (string v in fe.Versions)
                {
                    candidates.Add((edition, v, fe.Options));
                }
            }
        }

        if (candidates.Count == 0)
        {
            Console.Error.WriteLine("options-preview: the feed offers nothing to choose between.");
            return 1;
        }

        var vm = new ViewModels.ModuleOptionsViewModel(
            "Install Aver Engine 0.1.0", candidates, candidates[0].Item3);
        var dlg = new ModuleOptionsWindow(vm);
        dlg.ShowDialog();
        return 0;
    }
}

