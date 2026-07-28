using Aver.Launcher.Core;
using Xunit;

namespace Aver.Launcher.Tests;

/// <summary>
/// The verdict rules, tested against hand-written capabilities rather than only against whatever GPU
/// is present. The machine this was written on satisfies everything, so without these the "not
/// supported" branches would never execute anywhere.
/// </summary>
public class PrereqReportTests
{
    /// <summary>A machine that satisfies everything, matching the dev box.</summary>
    private static PrereqReport Good() => new()
    {
        Caps = new DeviceCaps
        {
            D3D12 = true, AdapterName = "AMD Radeon RX 7800 XT",
            DedicatedVideoMemory = 16177UL * 1024 * 1024,
            MsaaMask = 0xF, MaxMsaaSamples = 8, RayTracingTier = 11, ShaderModel = 66,
            MeshShaderTier = 1, TypedUavLoads = true, ConservativeRaster = true, ResourceBindingTier = 3,
        },
        Os = new OsInfo { Build = 26200, UpdateBuildRevision = 8894, DisplayVersion = "25H2" },
        VcRedist = new VcRedistInfo { Installed = true, Major = 14, Minor = 51, Version = "v14.51.36247.00" },
        Dotnet = new DotnetInfo { Root = @"C:\Program Files\dotnet" },
    };

    private static PrereqReport WithDotnet(PrereqReport r, string[] runtimes, string[] sdks)
    {
        r.Dotnet.Runtimes.AddRange(runtimes);
        r.Dotnet.Sdks.AddRange(sdks);
        return r;
    }

    private static Prereq Find(PrereqReport r, string name)
        => r.Evaluate().Single(p => p.Name == name);

    [Fact]
    public void A_satisfied_machine_has_nothing_blocking()
    {
        PrereqReport r = WithDotnet(Good(), ["10.0.10", "9.0.18"], ["10.0.302"]);
        Assert.True(r.CanRunEngine);
        Assert.All(r.Evaluate(), p => Assert.Equal(PrereqSeverity.Ok, p.Severity));
    }

    [Fact]
    public void No_d3d12_is_the_only_hardware_gate_that_blocks()
    {
        PrereqReport r = WithDotnet(Good(), ["10.0.10"], ["10.0.302"]);
        r.Caps = new DeviceCaps { D3D12 = false, Failure = "no Direct3D 12 runtime (hr=0x887A0004)" };

        Assert.False(r.CanRunEngine);
        Prereq p = Find(r, "Direct3D 12");
        Assert.Equal(PrereqSeverity.Blocking, p.Severity);
        Assert.Contains("hr=0x887A0004", p.Detail);
        // The feature rows are suppressed when there is no device: reporting "ray tracing: not
        // supported" under "the engine will not start" is noise, not information.
        Assert.DoesNotContain(r.Evaluate(), x => x.Name == "Ray tracing");
    }

    [Fact]
    public void Blocking_items_sort_first()
    {
        PrereqReport r = WithDotnet(Good(), [], []);
        r.Os.Build = 10240;   // Windows 10 1507, below the floor
        Assert.Equal(PrereqSeverity.Blocking, r.Evaluate()[0].Severity);
    }

    // ---------------------------------------------------------------- Windows floor

    [Theory]
    [InlineData(17134, PrereqSeverity.Ok)]        // 1803 exactly: IDXGIFactory6 arrives
    [InlineData(17133, PrereqSeverity.Blocking)]  // one build short
    [InlineData(10240, PrereqSeverity.Blocking)]  // 1507
    [InlineData(26200, PrereqSeverity.Ok)]
    public void Windows_floor_is_build_17134(int build, PrereqSeverity expected)
    {
        PrereqReport r = WithDotnet(Good(), ["10.0.10"], ["10.0.302"]);
        r.Os.Build = build;
        Assert.Equal(expected, Find(r, "Windows version").Severity);
    }

    [Fact]
    public void Family_comes_from_the_build_number_not_from_ProductName()
    {
        // ProductName reads "Windows 10 Pro" on this Windows 11 machine, so it is never consulted.
        Assert.Equal("Windows 11", new OsInfo { Build = 26200 }.Family);
        Assert.Equal("Windows 11", new OsInfo { Build = 22000 }.Family);
        Assert.Equal("Windows 10", new OsInfo { Build = 19045 }.Family);
    }

    // ---------------------------------------------------------------- the VCRUNTIME140_1 trap

    [Fact]
    public void Vc_redist_14_00_is_rejected_even_though_it_is_major_14()
    {
        // The VS2015 redist is 14.0 and does NOT carry VCRUNTIME140_1.dll, which the Release
        // Sandbox.exe imports. A naive "major == 14" check passes here and then the editor dies at
        // process start with a missing-DLL dialog.
        PrereqReport r = WithDotnet(Good(), ["10.0.10"], ["10.0.302"]);
        r.VcRedist = new VcRedistInfo { Installed = true, Major = 14, Minor = 0, Version = "v14.0.24215.01" };

        Prereq p = Find(r, "Visual C++ runtime");
        Assert.Equal(PrereqSeverity.Blocking, p.Severity);
        Assert.Contains("VCRUNTIME140_1.dll", p.Consequence);
        Assert.Equal(PrereqReport.VcRedistUrl, p.FixUrl);
        Assert.False(r.CanRunEngine);
    }

    [Theory]
    [InlineData(14, 20, PrereqSeverity.Ok)]         // VS2019 16.0, the first with VCRUNTIME140_1
    [InlineData(14, 19, PrereqSeverity.Blocking)]
    [InlineData(14, 51, PrereqSeverity.Ok)]
    [InlineData(15, 0, PrereqSeverity.Ok)]
    public void Vc_redist_floor_is_14_20(int major, int minor, PrereqSeverity expected)
    {
        PrereqReport r = WithDotnet(Good(), ["10.0.10"], ["10.0.302"]);
        r.VcRedist = new VcRedistInfo { Installed = true, Major = major, Minor = minor };
        Assert.Equal(expected, Find(r, "Visual C++ runtime").Severity);
    }

    [Fact]
    public void Loadable_dlls_satisfy_the_check_when_the_registry_does_not()
    {
        // Functional presence beats registry state: if the loader can map the DLLs, they are there
        // regardless of what the uninstall bookkeeping says.
        PrereqReport r = WithDotnet(Good(), ["10.0.10"], ["10.0.302"]);
        r.VcRedist = new VcRedistInfo { Installed = false };
        r.VcRedist.LoadableDlls.AddRange(["MSVCP140.dll", "VCRUNTIME140.dll", "VCRUNTIME140_1.dll"]);
        Assert.Equal(PrereqSeverity.Ok, Find(r, "Visual C++ runtime").Severity);
    }

    // ---------------------------------------------------------------- .NET rollForward semantics

    [Theory]
    [InlineData("10.0.0", true)]
    [InlineData("10.0.10", true)]
    [InlineData("10.0.302", true)]
    [InlineData("9.0.18", false)]
    [InlineData("8.0.29", false)]
    // rollForward: LatestMinor rolls forward across PATCH within 10.0, and does NOT cross a major.
    [InlineData("11.0.0", false)]
    [InlineData("10.1.0", false)]
    [InlineData("nonsense", false)]
    [InlineData("10", false)]
    public void Only_10_0_x_satisfies_the_bridge(string version, bool expected)
        => Assert.Equal(expected, PrereqReport.IsDotnet10(version));

    [Fact]
    public void Missing_dotnet_runtime_degrades_rather_than_blocks()
    {
        // The editor starts without .NET: nethost is LoadLibrary'd rather than linked, so the
        // scripting host declines instead of the process failing to load.
        PrereqReport r = WithDotnet(Good(), ["9.0.18", "8.0.29"], []);

        Prereq runtime = Find(r, ".NET 10 runtime");
        Assert.Equal(PrereqSeverity.Degraded, runtime.Severity);
        Assert.Contains("C# scripts will not run", runtime.Consequence);
        Assert.True(r.CanRunEngine);   // still worth downloading
    }

    [Fact]
    public void Missing_sdk_names_the_exact_feature_that_breaks()
    {
        PrereqReport r = WithDotnet(Good(), ["10.0.10"], []);
        Prereq sdk = Find(r, ".NET 10 SDK");
        Assert.Equal(PrereqSeverity.Degraded, sdk.Severity);
        // "missing" is not actionable; naming the menu item is.
        Assert.Contains("Compile Scripts", sdk.Consequence);
        Assert.True(r.CanRunEngine);
    }

    [Fact]
    public void An_sdk_without_a_runtime_still_degrades_the_runtime_row()
    {
        // Installing the SDK normally brings a runtime, but they are probed independently and the
        // rows must not infer one from the other.
        PrereqReport r = WithDotnet(Good(), [], ["10.0.302"]);
        Assert.Equal(PrereqSeverity.Degraded, Find(r, ".NET 10 runtime").Severity);
        Assert.Equal(PrereqSeverity.Ok, Find(r, ".NET 10 SDK").Severity);
    }

    // ---------------------------------------------------------------- capability-gated features

    [Fact]
    public void Dxr_10_degrades_and_explains_the_fallback()
    {
        PrereqReport r = WithDotnet(Good(), ["10.0.10"], ["10.0.302"]);
        r.Caps.RayTracingTier = 10;
        Prereq p = Find(r, "Ray tracing");
        Assert.Equal(PrereqSeverity.Degraded, p.Severity);
        Assert.Equal("DXR 1.0", p.Detail);
        Assert.Contains("PCF shadow map", p.Consequence);
    }

    [Fact]
    public void An_old_windows_build_is_named_as_the_reason_ray_tracing_is_absent()
    {
        // On build 18362 the GPU may be capable while the OS is not, and "your GPU does not support
        // it" would send the user shopping for hardware they already have.
        PrereqReport r = WithDotnet(Good(), ["10.0.10"], ["10.0.302"]);
        r.Os.Build = 18362;
        r.Caps.RayTracingTier = 0;
        Assert.Contains($"Windows build {PrereqReport.Dxr11WindowsBuild}", Find(r, "Ray tracing").Consequence);
    }

    [Fact]
    public void Every_feature_row_below_the_bar_degrades_but_none_blocks()
    {
        PrereqReport r = WithDotnet(Good(), ["10.0.10"], ["10.0.302"]);
        r.Caps.RayTracingTier = 0;
        r.Caps.MeshShaderTier = 0;
        r.Caps.MaxMsaaSamples = 1;
        r.Caps.MsaaMask = 1;
        r.Caps.ShaderModel = 51;
        r.Caps.ConservativeRaster = false;

        Assert.True(r.CanRunEngine);   // every one of these has a working fallback
        foreach (string name in new[]
                 { "Ray tracing", "Mesh shaders", "MSAA", "Shader model", "Conservative rasterisation" })
        {
            Prereq p = Find(r, name);
            Assert.Equal(PrereqSeverity.Degraded, p.Severity);
            Assert.False(string.IsNullOrWhiteSpace(p.Consequence), $"{name} degraded with no consequence");
        }
    }

    [Fact]
    public void Every_non_ok_row_says_what_the_user_loses()
    {
        // A warning with no consequence is a warning the user cannot act on.
        PrereqReport r = WithDotnet(Good(), [], []);
        r.Caps = new DeviceCaps { D3D12 = false, Failure = "none" };
        r.Os.Build = 10240;
        r.VcRedist = new VcRedistInfo();

        Assert.All(
            r.Evaluate().Where(p => p.Severity != PrereqSeverity.Ok),
            p => Assert.False(string.IsNullOrWhiteSpace(p.Consequence), $"{p.Name} has no consequence"));
    }

    [Theory]
    [InlineData(66u, "SM 6.6")]
    [InlineData(65u, "SM 6.5")]
    [InlineData(51u, "SM 5.1")]
    [InlineData(60u, "SM 6.0")]
    [InlineData(0u, "unknown")]
    public void Shader_model_formats_the_engine_encoding(uint sm, string expected)
        => Assert.Equal(expected, PrereqReport.FormatSm(sm));
}
