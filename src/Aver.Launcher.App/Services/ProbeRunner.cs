using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Aver.Launcher.Core;

namespace Aver.Launcher.App.Services;

/// <summary>
/// Runs the hardware probe in a child process and reads the result back as JSON.
/// </summary>
/// <remarks>
/// <para>
/// The probe creates a real Direct3D 12 device, which means it executes vendor driver code. That is
/// precisely the operation most likely to fault on the machines whose capabilities the user most needs
/// reported, and a launcher that dies inside a driver can never say so. So it runs out of process:
/// a crash there costs one exit code, and the UI reports "the probe crashed" instead of vanishing.
/// </para>
/// <para>
/// The child is THIS executable re-invoked with <c>--probe --json</c>, not a second binary, so the
/// self-contained single-file publish stays one file.
/// </para>
/// </remarks>
public static class ProbeRunner
{
    /// <summary>The argument that makes this process behave as a probe rather than as the launcher.</summary>
    public const string ProbeArgument = "--probe";

    public const string JsonArgument = "--json";

    public sealed record Result(PrereqReport? Report, string? Error);

    public static async Task<Result> RunAsync(CancellationToken ct = default)
    {
        string exe = Environment.ProcessPath ?? string.Empty;
        if (exe.Length == 0 || !File.Exists(exe))
        {
            return new Result(null, "could not locate the launcher executable to re-invoke");
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(ProbeArgument);
        psi.ArgumentList.Add(JsonArgument);

        try
        {
            using Process? p = Process.Start(psi);
            if (p is null) return new Result(null, "the probe process did not start");

            Task<string> stdout = p.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = p.StandardError.ReadToEndAsync(ct);

            // A driver that hangs is as bad as one that faults, so the wait is bounded.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new Result(null, "the hardware probe timed out after 20 seconds");
            }

            string json = await stdout.ConfigureAwait(false);
            string err = await stderr.ConfigureAwait(false);

            if (json.Length == 0)
            {
                return new Result(null,
                    p.ExitCode is < 0 or > 1
                        ? $"the hardware probe crashed (exit 0x{p.ExitCode:X8}). {err}".TrimEnd()
                        : $"the hardware probe produced no output. {err}".TrimEnd());
            }

            PrereqReport? report = JsonSerializer.Deserialize<PrereqReport>(json, JsonOpts);
            return report is null
                ? new Result(null, "the hardware probe returned unreadable output")
                : new Result(report, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            return new Result(null, $"the hardware probe failed: {ex.Message}");
        }
    }

    /// <summary>Serialises a report for the child process to write.</summary>
    public static string Serialise(PrereqReport report) => JsonSerializer.Serialize(report, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };
}
