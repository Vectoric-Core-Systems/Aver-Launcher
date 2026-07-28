using System.Runtime.InteropServices;

namespace Aver.Launcher.Platform;

/// <summary>
/// Attaches a GUI process to the console that started it.
/// </summary>
/// <remarks>
/// The launcher is a <c>WinExe</c>, so Windows gives it no console. That is right for the UI and wrong
/// for the diagnostic modes: run <c>AverLauncher.exe --probe</c> from a terminal and, without this, the
/// output goes nowhere and the command looks like it silently did nothing.
/// <para>
/// Only needed for the human-readable form. When the launcher spawns itself with <c>--json</c> the
/// parent redirects stdout, so the pipe already exists and attaching would be pointless.
/// </para>
/// </remarks>
public static partial class ConsoleAttach
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    /// <summary>Attaches to the parent's console if there is one. Silent when there is not.</summary>
    public static bool ToParent() => AttachConsole(AttachParentProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint dwProcessId);
}
