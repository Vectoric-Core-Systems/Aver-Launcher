using System.Text.Json;
using Aver.Launcher.Core;
using Aver.Launcher.Platform;

namespace Aver.Launcher.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        string verb = args.Length > 0 ? args[0] : "help";
        bool json = args.Contains("--json", StringComparer.Ordinal);

        return verb switch
        {
            "probe" => Probe(json),
            "probe-gpu" => ProbeGpu(json),
            _ => Help(),
        };
    }

    private static int Help()
    {
        Console.WriteLine("""
            averlauncher-cli <verb> [--json]

              probe       full prerequisite report (GPU, Windows, VC++ runtime, .NET)
              probe-gpu   Direct3D 12 capabilities only; this is the child-process form

            Exit codes: 0 = nothing blocking, 1 = a blocking prerequisite failed, 2 = usage.
            """);
        return 2;
    }

    /// <summary>
    /// GPU only. This is what the WPF app spawns, so its contract is narrow on purpose: JSON on
    /// stdout, nothing else, and a crash here takes down only this process.
    /// </summary>
    private static int ProbeGpu(bool json)
    {
        DeviceCaps caps = D3D12Probe.Query();
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(caps, JsonOpts));
        }
        else
        {
            Console.WriteLine($"D3D12            {(caps.D3D12 ? "yes" : "NO")}");
            Console.WriteLine($"adapter          {caps.AdapterName}");
            Console.WriteLine($"ray tracing      tier {caps.RayTracingTier}");
            Console.WriteLine($"max MSAA         {caps.MaxMsaaSamples}x  (mask 0x{caps.MsaaMask:X})");
            Console.WriteLine($"shader model     {caps.ShaderModel}");
            Console.WriteLine($"mesh shader tier {caps.MeshShaderTier}");
            Console.WriteLine($"typed UAV loads  {caps.TypedUavLoads}");
            Console.WriteLine($"cons. raster     {caps.ConservativeRaster}");
            Console.WriteLine($"binding tier     {caps.ResourceBindingTier}");
            if (caps.Failure is not null) Console.WriteLine($"failure          {caps.Failure}");
        }
        return caps.D3D12 ? 0 : 1;
    }

    private static int Probe(bool json)
    {
        PrereqReport report = SystemProbe.QueryAll(out string? warning);
        IReadOnlyList<Prereq> items = report.Evaluate();

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { report.Caps, report.Os, report.Dotnet, report.VcRedist, items, warning }, JsonOpts));
        }
        else
        {
            if (warning is not null) Console.WriteLine($"! {warning}");
            Console.WriteLine($"{report.Os.Family} build {report.Os.Build}.{report.Os.UpdateBuildRevision} "
                              + $"{report.Os.DisplayVersion}".TrimEnd());
            Console.WriteLine($"dotnet root      {report.Dotnet.Root ?? "(not found)"}");
            Console.WriteLine($"  runtimes       {Join(report.Dotnet.Runtimes)}");
            Console.WriteLine($"  sdks           {Join(report.Dotnet.Sdks)}");
            Console.WriteLine($"vc++ runtime     {report.VcRedist.Version} "
                              + $"[{Join(report.VcRedist.LoadableDlls)}]");
            Console.WriteLine();
            foreach (Prereq p in items)
            {
                string mark = p.Severity switch
                {
                    PrereqSeverity.Ok => "  ok  ",
                    PrereqSeverity.Degraded => " warn ",
                    _ => " FAIL ",
                };
                Console.WriteLine($"[{mark}] {p.Name,-28} {p.Detail}");
                if (p.Consequence is not null) Console.WriteLine($"           {p.Consequence}");
                if (p.FixUrl is not null) Console.WriteLine($"           -> {p.FixUrl}");
            }
            Console.WriteLine();
            Console.WriteLine(report.CanRunEngine
                ? "VERDICT: this machine can run Aver Engine."
                : "VERDICT: this machine cannot run Aver Engine.");
        }

        return report.CanRunEngine ? 0 : 1;

        static string Join(List<string> v) => v.Count == 0 ? "(none)" : string.Join(", ", v);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };
}
