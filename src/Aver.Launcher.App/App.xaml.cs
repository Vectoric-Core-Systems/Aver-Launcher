using System.Windows;
using Aver.Launcher.App.Services;
using Aver.Launcher.Core;
using Aver.Launcher.Platform;

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
}
