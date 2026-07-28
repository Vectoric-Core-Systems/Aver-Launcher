namespace Aver.Launcher.Core;

/// <summary>How badly a single prerequisite failing hurts.</summary>
public enum PrereqSeverity
{
    /// <summary>Satisfied.</summary>
    Ok,

    /// <summary>The engine runs, but something the user may care about is unavailable.</summary>
    Degraded,

    /// <summary>The engine will not start. Downloading it would waste the user's bandwidth.</summary>
    Blocking,
}

/// <summary>One checked prerequisite.</summary>
/// <param name="Name">Short label, e.g. "Direct3D 12".</param>
/// <param name="Severity">Consequence of this one failing.</param>
/// <param name="Detail">What was actually found -- a version, a tier, a build number.</param>
/// <param name="Consequence">
/// What the user loses. Required when not <see cref="PrereqSeverity.Ok"/>: "missing" is not
/// actionable, "Tools > Compile Scripts will fail" is.
/// </param>
/// <param name="FixUrl">Where to get it, when there is somewhere.</param>
public sealed record Prereq(
    string Name,
    PrereqSeverity Severity,
    string Detail,
    string? Consequence = null,
    string? FixUrl = null);

/// <summary>
/// Hardware and runtime capabilities, in the ENGINE's own encoding.
/// </summary>
/// <remarks>
/// Field encodings are copied from <c>aver::rhi::DeviceCaps</c> so a value can be compared directly
/// against what the engine logs, with no translation step to get wrong:
/// ray-tracing tier is 0/10/11, shader model is 51/60/65/66, mesh-shader tier is 0/1.
/// </remarks>
public sealed class DeviceCaps
{
    public bool D3D12 { get; set; }
    public string AdapterName { get; set; } = string.Empty;
    public uint VendorId { get; set; }
    public uint DeviceId { get; set; }
    public ulong DedicatedVideoMemory { get; set; }
    public bool IsSoftwareAdapter { get; set; }

    /// <summary>Bit N set means N samples supported (bits 1, 2, 4, 8).</summary>
    public uint MsaaMask { get; set; } = 1;
    public uint MaxMsaaSamples { get; set; } = 1;

    /// <summary>0 = none, 10 = DXR 1.0, 11 = DXR 1.1.</summary>
    public uint RayTracingTier { get; set; }

    /// <summary>51 = SM 5.1, 60 = SM 6.0, ... The engine caps its query at 66, so this does too.</summary>
    public uint ShaderModel { get; set; }

    /// <summary>0 = none, 1 = Tier 1.</summary>
    public uint MeshShaderTier { get; set; }

    public bool TypedUavLoads { get; set; }
    public bool ConservativeRaster { get; set; }

    /// <summary>0 = unknown, 1/2/3 = D3D12_RESOURCE_BINDING_TIER_N.</summary>
    public uint ResourceBindingTier { get; set; }

    /// <summary>Set when the probe could not create a device; explains why.</summary>
    public string? Failure { get; set; }
}

/// <summary>Windows version, read from the fields that do not lie.</summary>
public sealed class OsInfo
{
    public int Build { get; set; }
    public int UpdateBuildRevision { get; set; }

    /// <summary>e.g. "25H2". Empty on older builds that predate the value.</summary>
    public string DisplayVersion { get; set; } = string.Empty;

    /// <summary>Derived from <see cref="Build"/>, not from ProductName -- see the remarks on the probe.</summary>
    public string Family => Build >= 22000 ? "Windows 11" : "Windows 10";
}

/// <summary>.NET runtimes and SDKs found on the machine.</summary>
public sealed class DotnetInfo
{
    public string? Root { get; set; }

    /// <summary>Versions under <c>shared\Microsoft.NETCore.App</c>.</summary>
    public List<string> Runtimes { get; } = [];

    /// <summary>Versions under <c>sdk</c>.</summary>
    public List<string> Sdks { get; } = [];
}

/// <summary>Visual C++ redistributable, as the registry and the loader report it.</summary>
public sealed class VcRedistInfo
{
    public bool Installed { get; set; }
    public int Major { get; set; }
    public int Minor { get; set; }
    public int Build { get; set; }
    public string Version { get; set; } = string.Empty;

    /// <summary>Runtime DLLs that actually loaded, which beats registry state.</summary>
    public List<string> LoadableDlls { get; } = [];
}

/// <summary>
/// The whole machine check, and the rules that turn raw capabilities into a verdict.
/// </summary>
/// <remarks>
/// The thresholds live here, in a project with no P/Invoke, so they can be tested against
/// hand-written inputs rather than only against whatever GPU happens to be present. Sources are
/// <c>docs/MINIMUM_SPECS.md</c> §2/§5/§6 and the engine's own <c>queryCaps</c>.
/// </remarks>
public sealed class PrereqReport
{
    public DeviceCaps Caps { get; set; } = new();
    public OsInfo Os { get; set; } = new();
    public DotnetInfo Dotnet { get; set; } = new();
    public VcRedistInfo VcRedist { get; set; } = new();

    // ---- thresholds, each with the reason it is that number ----

    /// <summary>Windows 10 1803. The engine requires <c>IDXGIFactory6</c>, which arrived here.</summary>
    public const int MinWindowsBuild = 17134;

    /// <summary>1809 -- DXR 1.0.</summary>
    public const int Dxr10WindowsBuild = 17763;

    /// <summary>2004 -- DXR 1.1, which is what Voxi's inline RayQuery path needs.</summary>
    public const int Dxr11WindowsBuild = 19041;

    /// <summary>
    /// VC++ 14.20 (VS2019 16.0). NOT just "14.x": the Release <c>Sandbox.exe</c> imports
    /// <c>VCRUNTIME140_1.dll</c>, which does not exist in the VS2015 redist -- so a machine carrying
    /// only 14.0 passes a naive major-version check and then dies with a missing-DLL dialog.
    /// </summary>
    public const int VcRedistMinMajor = 14;

    /// <inheritdoc cref="VcRedistMinMajor"/>
    public const int VcRedistMinMinor = 20;

    /// <summary>
    /// The managed bridge's <c>runtimeconfig.json</c> asks for <c>Microsoft.NETCore.App 10.0.0</c>
    /// with <c>rollForward: LatestMinor</c>, so ANY 10.0.x satisfies it and 11.x does not.
    /// </summary>
    public const int DotnetMajor = 10;

    public const string VcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
    public const string DotnetUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";

    /// <summary>True when a version string names a <c>10.0.x</c> release.</summary>
    public static bool IsDotnet10(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        string[] parts = version.Split('.');
        return parts.Length >= 2
               && int.TryParse(parts[0], out int major) && major == DotnetMajor
               && int.TryParse(parts[1], out int minor) && minor == 0;
    }

    /// <summary>Evaluates every prerequisite, worst first.</summary>
    public IReadOnlyList<Prereq> Evaluate()
    {
        var items = new List<Prereq>();

        // --- D3D12: the only hard gate. Vulkan and D3D11 backends are stubs, so "supports
        //     DirectX 12" is a requirement rather than a preference.
        items.Add(Caps.D3D12
            ? new Prereq("Direct3D 12", PrereqSeverity.Ok,
                Caps.IsSoftwareAdapter
                    ? $"{Caps.AdapterName} (software)"
                    : $"{Caps.AdapterName}, {Caps.DedicatedVideoMemory / (1024 * 1024)} MB")
            : new Prereq("Direct3D 12", PrereqSeverity.Blocking,
                Caps.Failure ?? "no Direct3D 12 device",
                "The engine will not start. D3D12 is the only working render backend."));

        // --- OS floor.
        string osDetail = $"{Os.Family} build {Os.Build}.{Os.UpdateBuildRevision}"
                          + (Os.DisplayVersion.Length > 0 ? $" ({Os.DisplayVersion})" : string.Empty);
        items.Add(Os.Build >= MinWindowsBuild
            ? new Prereq("Windows version", PrereqSeverity.Ok, osDetail)
            : new Prereq("Windows version", PrereqSeverity.Blocking, osDetail,
                $"Build {MinWindowsBuild} (Windows 10 1803) or later is required for IDXGIFactory6."));

        // --- VC++ runtime. Functional presence beats registry state, so a loadable
        //     VCRUNTIME140_1.dll counts even when the registry is unhelpful.
        bool vcVersionOk = VcRedist.Major > VcRedistMinMajor
                           || (VcRedist.Major == VcRedistMinMajor && VcRedist.Minor >= VcRedistMinMinor);
        bool vcLoadable = VcRedist.LoadableDlls.Contains("VCRUNTIME140_1.dll")
                          && VcRedist.LoadableDlls.Contains("MSVCP140.dll");
        items.Add(vcLoadable || (VcRedist.Installed && vcVersionOk)
            ? new Prereq("Visual C++ runtime", PrereqSeverity.Ok,
                VcRedist.Version.Length > 0 ? VcRedist.Version : string.Join(", ", VcRedist.LoadableDlls))
            : new Prereq("Visual C++ runtime", PrereqSeverity.Blocking,
                VcRedist.Installed ? $"found {VcRedist.Major}.{VcRedist.Minor}" : "not found",
                "The editor will not start: it needs VCRUNTIME140_1.dll from the "
                + "VC++ 2015-2022 redistributable (14.20 or later).",
                VcRedistUrl));

        // --- .NET 10 runtime: scripting only. The editor starts without it, because nethost is
        //     LoadLibrary'd rather than linked, and the scripting host declines instead of failing.
        string? runtime10 = Dotnet.Runtimes.FirstOrDefault(IsDotnet10);
        items.Add(runtime10 is not null
            ? new Prereq(".NET 10 runtime", PrereqSeverity.Ok, runtime10)
            : new Prereq(".NET 10 runtime", PrereqSeverity.Degraded,
                Dotnet.Runtimes.Count > 0 ? $"found {string.Join(", ", Dotnet.Runtimes)}" : "not found",
                "The editor starts and renders, but C# scripts will not run: the managed bridge "
                + "requires Microsoft.NETCore.App 10.0.x (rollForward LatestMinor, so 11.x does not count).",
                DotnetUrl));

        // --- .NET SDK: authoring only.
        string? sdk10 = Dotnet.Sdks.FirstOrDefault(IsDotnet10);
        items.Add(sdk10 is not null
            ? new Prereq(".NET 10 SDK", PrereqSeverity.Ok, sdk10)
            : new Prereq(".NET 10 SDK", PrereqSeverity.Degraded,
                Dotnet.Sdks.Count > 0 ? $"found {string.Join(", ", Dotnet.Sdks)}" : "not found",
                "Tools > Compile Scripts will fail: the editor shells out to `dotnet build`. "
                + "Playing existing compiled scripts still works.",
                DotnetUrl));

        // --- Capability-gated rendering features. Informational: every one has a working fallback.
        if (Caps.D3D12)
        {
            items.Add(Caps.RayTracingTier >= 11
                ? new Prereq("Ray tracing", PrereqSeverity.Ok, "DXR 1.1")
                : new Prereq("Ray tracing", PrereqSeverity.Degraded,
                    Caps.RayTracingTier == 10 ? "DXR 1.0" : "not supported",
                    Os.Build < Dxr11WindowsBuild
                        ? $"Inline RayQuery needs DXR 1.1 and Windows build {Dxr11WindowsBuild}; "
                          + "shadows fall back to a 3x3 PCF shadow map."
                        : "Inline RayQuery is unavailable; shadows fall back to a 3x3 PCF shadow map."));

            items.Add(Caps.MeshShaderTier >= 1
                ? new Prereq("Mesh shaders", PrereqSeverity.Ok, "Tier 1")
                : new Prereq("Mesh shaders", PrereqSeverity.Degraded, "not supported",
                    "Voxelisation uses the geometry-shader path instead. Correct, and not the "
                    + "cause of any known slowdown."));

            items.Add(Caps.MaxMsaaSamples > 1
                ? new Prereq("MSAA", PrereqSeverity.Ok, $"up to {Caps.MaxMsaaSamples}x")
                : new Prereq("MSAA", PrereqSeverity.Degraded, "1x only",
                    "Edges will alias; there is no post-process antialiasing stage."));

            items.Add(Caps.ShaderModel >= 65
                ? new Prereq("Shader model", PrereqSeverity.Ok, FormatSm(Caps.ShaderModel))
                : new Prereq("Shader model", PrereqSeverity.Degraded, FormatSm(Caps.ShaderModel),
                    "Mesh shaders and inline RayQuery both need SM 6.5."));

            items.Add(Caps.ConservativeRaster
                ? new Prereq("Conservative rasterisation", PrereqSeverity.Ok, "supported")
                : new Prereq("Conservative rasterisation", PrereqSeverity.Degraded, "not supported",
                    "Voxelisation is not watertight, so global illumination may leak light."));
        }

        return items.OrderByDescending(i => i.Severity switch
        {
            PrereqSeverity.Blocking => 2,
            PrereqSeverity.Degraded => 1,
            _ => 0,
        }).ToList();
    }

    /// <summary>"66" -&gt; "SM 6.6".</summary>
    public static string FormatSm(uint sm)
        => sm == 0 ? "unknown" : $"SM {sm / 10}.{sm % 10}";

    /// <summary>True when nothing blocking was found -- i.e. downloading the engine is worthwhile.</summary>
    public bool CanRunEngine => !Evaluate().Any(i => i.Severity == PrereqSeverity.Blocking);
}
