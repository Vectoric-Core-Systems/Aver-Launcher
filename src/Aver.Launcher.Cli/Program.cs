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
            "edition" => Edition(args, json),
            _ => Help(),
        };
    }

    private static int Help()
    {
        Console.WriteLine("""
            averlauncher-cli <verb> [--json]

              probe             full prerequisite report (GPU, Windows, VC++ runtime, .NET)
              probe-gpu         Direct3D 12 capabilities only; this is the child-process form
              edition <path>    describe a staged payload: version, modules, what it gave up

            Exit codes: 0 = ok, 1 = a blocking prerequisite failed or the payload is incomplete,
                        2 = usage.
            """);
        return 2;
    }

    /// <summary>
    /// Describes a staged payload from its <c>payload.json</c>.
    /// </summary>
    /// <remarks>
    /// This is the read side of the edition model: the launcher's UI shows exactly this, so having it
    /// as a verb means the description can be checked against a real build tree rather than only
    /// against hand-written test data.
    /// </remarks>
    private static int Edition(string[] args, bool json)
    {
        string? path = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        if (path is null)
        {
            Console.Error.WriteLine("edition: expected a path to a staged payload or its payload.json");
            return 2;
        }

        string file = Directory.Exists(path) ? Path.Combine(path, "payload.json") : path;
        if (!PayloadInfo.TryLoad(file, out PayloadInfo? info, out string? error))
        {
            Console.Error.WriteLine($"edition: {error}");
            return 1;
        }

        string id = Path.GetFileName(Path.TrimEndingDirectorySeparator(
            Directory.Exists(path) ? path : Path.GetDirectoryName(file) ?? path));
        EngineEdition ed = info!.ToEdition(id);
        bool complete = ed.IsComplete(out IReadOnlyList<string> missing);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                info.Version, info.Config, info.SourceCommit, info.SourceDirty,
                info.FileCount, info.TotalBytes,
                edition = new
                {
                    ed.Id, ed.DisplayName, ed.ModuleSummary, ed.CanEditProjects,
                    included = ed.IncludedModules,
                    excluded = ed.ExcludedModules.Select(x => new { module = x.Module, consequence = x.Consequence }),
                    backends = ed.Backends,
                },
                complete, missing,
            }, JsonOpts));
            return complete ? 0 : 1;
        }

        Console.WriteLine($"Aver Engine {info.Version} ({info.Config})   edition '{ed.Id}'");
        Console.WriteLine($"  {info.FileCount} files, {info.TotalBytes / (1024.0 * 1024.0):F2} MB");
        Console.WriteLine($"  built from {Short(info.SourceCommit)}{(info.SourceDirty ? " (dirty tree)" : string.Empty)}");
        Console.WriteLine($"  entry point {info.EntryPoint}");
        Console.WriteLine();
        Console.WriteLine($"  backends        {Join(ed.Backends)}");
        Console.WriteLine($"  modules in      {Join(ed.IncludedModules)}");
        if (ed.ExcludedModules.Count > 0)
        {
            Console.WriteLine("  modules out");
            foreach ((string module, string consequence) in ed.ExcludedModules)
            {
                Console.WriteLine($"    {module,-22} {consequence}");
            }
        }
        Console.WriteLine();
        Console.WriteLine($"  always included {Join(EngineOptions.AlwaysIncluded)}");
        Console.WriteLine($"  opens projects  {(ed.CanEditProjects ? "yes" : "NO")}");

        if (!complete)
        {
            Console.WriteLine();
            Console.WriteLine("  INCOMPLETE: payload.json does not record " + string.Join(", ", missing));
            Console.WriteLine("  An option that is absent is not an option that is OFF - what this build");
            Console.WriteLine("  contains cannot be stated, so it must not be published as an edition.");
        }

        return complete ? 0 : 1;

        static string Short(string commit) => commit.Length >= 7 ? commit[..7] : (commit.Length == 0 ? "?" : commit);
        static string Join(IEnumerable<string> v)
        {
            string s = string.Join(", ", v);
            return s.Length == 0 ? "(none)" : s;
        }
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
