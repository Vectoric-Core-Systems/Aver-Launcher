using System.Runtime.InteropServices;
using Aver.Launcher.Core;

namespace Aver.Launcher.Platform;

/// <summary>
/// Reports the GPU's Direct3D 12 capabilities by hosting its own minimal device.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not just ask the engine.</b> <c>docs/MINIMUM_SPECS.md</c> §7 suggests P/Invoking
/// <c>aver_voxi_ray_tracing_tier()</c>, and its sample code is wrong. That function returns whatever
/// <c>Renderer::setDeviceInfo</c> was last handed, and the only caller is <c>SandboxApp.cpp:726</c> --
/// so in a standalone process the caps are zero-initialised and an RTX owner is told they have no ray
/// tracing. A launcher whose whole job is answering "can this machine run it" cannot rely on that.
/// </para>
/// <para>
/// <b>Faithfulness is the point.</b> Every value is produced by the same sequence, in the same order,
/// with the same encoding as the engine's <c>D3D12Device::queryCaps</c>. Notably: MSAA is queried
/// against <c>R16G16B16A16_FLOAT</c> (the scene colour format -- querying <c>R8G8B8A8_UNORM</c> would
/// answer a question the engine never asks); the shader-model walk starts at 6.6 and therefore reports
/// at most 66 even on hardware supporting more; and mesh-shader tier is normalised to 0/1 rather than
/// D3D's 0/10.
/// </para>
/// <para>
/// COM is called through raw vtable slots, so this needs no COM registration and no marshalling layer.
/// Each method gets its own typed function pointer rather than a shared generic call, because these
/// methods differ in arity and a mismatched signature corrupts the stack instead of failing cleanly.
/// </para>
/// </remarks>
public static unsafe partial class D3D12Probe
{
    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory2(uint flags, in Guid riid, out nint factory);

    [LibraryImport("d3d12.dll")]
    private static partial int D3D12CreateDevice(
        nint adapter, uint minimumFeatureLevel, in Guid riid, out nint device);

    private static readonly Guid IID_IDXGIFactory6 = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
    private static readonly Guid IID_IDXGIAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");
    private static readonly Guid IID_ID3D12Device = new("189819f1-1db6-4b57-be54-1821339b85f7");

    // ---- vtable slots, by interface-inheritance order ----
    //
    // IUnknown       0 QueryInterface, 1 AddRef, 2 Release
    // IDXGIObject    3 SetPrivateData .. 6 GetParent
    // IDXGIFactory   7 EnumAdapters .. 11 CreateSoftwareAdapter
    // IDXGIFactory1 12 EnumAdapters1, 13 IsCurrent
    // IDXGIFactory2 14 IsWindowedStereoEnabled .. 24 CreateSwapChainForComposition
    // IDXGIFactory3 25 GetCreationFlags
    // IDXGIFactory4 26 EnumAdapterByLuid, 27 EnumWarpAdapter
    // IDXGIFactory5 28 CheckFeatureSupport
    // IDXGIFactory6 29 EnumAdapterByGpuPreference
    private const int VtRelease = 2;
    private const int VtEnumWarpAdapter = 27;
    private const int VtEnumAdapterByGpuPreference = 29;

    // IDXGIAdapter1: IUnknown 0..2, IDXGIObject 3..6, IDXGIAdapter 7 EnumOutputs / 8 GetDesc /
    // 9 CheckInterfaceSupport, then GetDesc1.
    private const int VtGetDesc1 = 10;

    // ID3D12Device: IUnknown 0..2, ID3D12Object 3..6, GetNodeCount 7, CreateCommandQueue 8,
    // CreateCommandAllocator 9, CreateGraphicsPipelineState 10, CreateComputePipelineState 11,
    // CreateCommandList 12, CheckFeatureSupport 13.
    private const int VtCheckFeatureSupport = 13;

    // D3D12_FEATURE values. These are NOT sequential and the gaps are not where they look: 4 is
    // MULTISAMPLE_QUALITY_LEVELS while 6 is GPU_VIRTUAL_ADDRESS_SUPPORT, and getting that pair the
    // wrong way round returns E_INVALIDARG (the size check fails against a different struct), which
    // reads as "this GPU has no MSAA" rather than as a bug.
    private const uint FeatureOptions = 0;                     // D3D12_FEATURE_D3D12_OPTIONS
    private const uint FeatureMultisampleQualityLevels = 4;     // ...MULTISAMPLE_QUALITY_LEVELS
    private const uint FeatureShaderModel = 7;                  // ...SHADER_MODEL
    private const uint FeatureOptions5 = 27;                    // ...D3D12_OPTIONS5
    private const uint FeatureOptions7 = 32;                    // ...D3D12_OPTIONS7

    /// <summary>
    /// Word count of D3D12_FEATURE_DATA_D3D12_OPTIONS. CheckFeatureSupport compares the size it is
    /// given against sizeof the struct it expects EXACTLY, so an over-large buffer is rejected
    /// outright rather than partially filled.
    /// </summary>
    private const int OptionsWordCount = 15;

    /// <summary>The engine's <c>kSceneColorFormat</c>: DXGI_FORMAT_R16G16B16A16_FLOAT.</summary>
    private const uint SceneColorFormat = 10;

    private const uint FeatureLevel11_0 = 0xb000;
    private const uint GpuPreferenceHighPerformance = 1;
    private const uint AdapterFlagSoftware = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FeatureDataMultisample
    {
        public uint Format;
        public uint SampleCount;
        public uint Flags;
        public uint NumQualityLevels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FeatureDataShaderModel
    {
        public uint HighestShaderModel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FeatureDataOptions5
    {
        public int SRVOnlyTiledResourceTier3;
        public uint RenderPassesTier;
        public uint RaytracingTier;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FeatureDataOptions7
    {
        public uint MeshShaderTier;
        public uint SamplerFeedbackTier;
    }

    private static void Release(nint obj)
    {
        if (obj == 0) return;
        void** vtbl = *(void***)obj;
        ((delegate* unmanaged<nint, uint>)vtbl[VtRelease])(obj);
    }

    private static int CheckFeature(nint device, uint feature, void* data, int size)
    {
        void** vtbl = *(void***)device;
        var fn = (delegate* unmanaged<nint, uint, void*, uint, int>)vtbl[VtCheckFeatureSupport];
        return fn(device, feature, data, (uint)size);
    }

    /// <summary>
    /// Probes the adapter the engine would pick. Never throws: a driver that refuses is a result,
    /// not an exception.
    /// </summary>
    public static DeviceCaps Query()
    {
        var caps = new DeviceCaps();
        nint factory = 0, adapter = 0, device = 0;

        try
        {
            int hr = CreateDXGIFactory2(0, IID_IDXGIFactory6, out factory);
            if (hr < 0 || factory == 0)
            {
                // IDXGIFactory6 IS the Windows 10 1803 marker the engine depends on, so failing to
                // obtain it is a precise below-floor diagnosis rather than a generic DXGI error.
                caps.Failure =
                    $"IDXGIFactory6 unavailable (hr=0x{hr:X8}) - Windows 10 1803 or later is required";
                return caps;
            }

            // The same adapter the engine asks for, so the caps reported are the caps it will see.
            // IDXGIFactory6::EnumAdapterByGpuPreference(UINT, DXGI_GPU_PREFERENCE, REFIID, void**)
            {
                Guid iid = IID_IDXGIAdapter1;
                void** vtbl = *(void***)factory;
                var fn = (delegate* unmanaged<nint, uint, uint, Guid*, nint*, int>)
                    vtbl[VtEnumAdapterByGpuPreference];
                nint local = 0;
                hr = fn(factory, 0, GpuPreferenceHighPerformance, &iid, &local);
                adapter = local;
            }
            if (hr < 0 || adapter == 0)
            {
                caps.Failure = $"no DXGI adapter (hr=0x{hr:X8})";
                return caps;
            }

            {
                var desc = default(AdapterDesc1);
                void** vtbl = *(void***)adapter;
                var fn = (delegate* unmanaged<nint, AdapterDesc1*, int>)vtbl[VtGetDesc1];
                if (fn(adapter, &desc) >= 0)
                {
                    // desc is a local, so its fixed buffer is already pinned; a `fixed` statement
                    // here would be taking the address of an already-fixed expression.
                    caps.AdapterName = new string(desc.Description);
                    caps.VendorId = desc.VendorId;
                    caps.DeviceId = desc.DeviceId;
                    caps.DedicatedVideoMemory = desc.DedicatedVideoMemory;
                    caps.IsSoftwareAdapter = (desc.Flags & AdapterFlagSoftware) != 0;
                }
            }

            hr = D3D12CreateDevice(adapter, FeatureLevel11_0, IID_ID3D12Device, out device);
            if (hr < 0 || device == 0)
            {
                caps.Failure = DiagnoseNoDevice(factory, hr);
                return caps;
            }

            caps.D3D12 = true;

            // ---- MSAA, against the engine's scene colour format, 2/4/8 in that order ----
            caps.MsaaMask = 1;
            caps.MaxMsaaSamples = 1;
            foreach (uint s in new uint[] { 2, 4, 8 })
            {
                var ms = new FeatureDataMultisample { Format = SceneColorFormat, SampleCount = s };
                if (CheckFeature(device, FeatureMultisampleQualityLevels, &ms, sizeof(FeatureDataMultisample)) >= 0
                    && ms.NumQualityLevels > 0)
                {
                    caps.MsaaMask |= s;
                    caps.MaxMsaaSamples = s;
                }
            }

            // ---- OPTIONS ----
            //
            // Read as raw 4-byte words and indexed explicitly rather than through a declared struct,
            // because D3D12_FEATURE_DATA_D3D12_OPTIONS has three BOOLs interleaved among the tiers and
            // a mis-declared field would silently read its neighbour. Word order in the header:
            //   0 DoublePrecisionFloatShaderOps      1 OutputMergerLogicOp
            //   2 MinPrecisionSupport                3 TiledResourcesTier
            //   4 ResourceBindingTier                5 PSSpecifiedStencilRefSupported
            //   6 TypedUAVLoadAdditionalFormats      7 ROVsSupported
            //   8 ConservativeRasterizationTier      9 MaxGPUVirtualAddressBitsPerResource
            //  10 StandardSwizzle64KBSupported      11 CrossNodeSharingTier
            //  12 CrossAdapterRowMajorTextureSupported
            //  13 VPAndRTArrayIndexFromAnyShaderFeedingRasterizerSupportedWithoutGSEmulation
            //  14 ResourceHeapTier                  -> 15 words, 60 bytes
            uint* opt = stackalloc uint[OptionsWordCount];
            for (int i = 0; i < OptionsWordCount; i++) opt[i] = 0;
            if (CheckFeature(device, FeatureOptions, opt, OptionsWordCount * sizeof(uint)) >= 0)
            {
                caps.ResourceBindingTier = opt[4];
                caps.TypedUavLoads = opt[6] != 0;
                caps.ConservativeRaster = opt[8] != 0;
            }

            // ---- shader model ----
            //
            // Starts at 6.6 exactly as the engine does. CheckFeatureSupport lowers the requested value
            // to the highest supported, so this reports at most 66 even on newer hardware. Agreeing
            // with the engine matters more than reporting the true ceiling.
            caps.ShaderModel = 0;
            foreach (uint sm in new uint[] { 0x66, 0x65, 0x61, 0x60 })
            {
                var q = new FeatureDataShaderModel { HighestShaderModel = sm };
                if (CheckFeature(device, FeatureShaderModel, &q, sizeof(FeatureDataShaderModel)) >= 0)
                {
                    caps.ShaderModel = 60 + (q.HighestShaderModel & 0x0F);
                    break;
                }
            }
            if (caps.ShaderModel < 60) caps.ShaderModel = 51;

            // ---- mesh shaders, normalised to the engine's 0/1 ----
            var o7 = default(FeatureDataOptions7);
            if (CheckFeature(device, FeatureOptions7, &o7, sizeof(FeatureDataOptions7)) >= 0)
                caps.MeshShaderTier = o7.MeshShaderTier >= 10 ? 1u : 0u;

            // ---- ray tracing. D3D's enum is already 0/10/11, which is why the engine reuses it. ----
            var o5 = default(FeatureDataOptions5);
            if (CheckFeature(device, FeatureOptions5, &o5, sizeof(FeatureDataOptions5)) >= 0)
            {
                if (o5.RaytracingTier >= 11) caps.RayTracingTier = 11;
                else if (o5.RaytracingTier >= 10) caps.RayTracingTier = 10;
            }

            return caps;
        }
        catch (DllNotFoundException ex)
        {
            caps.Failure = $"a Direct3D 12 system library is missing: {ex.Message}";
            return caps;
        }
        catch (EntryPointNotFoundException ex)
        {
            caps.Failure = $"a Direct3D 12 entry point is missing: {ex.Message}";
            return caps;
        }
        finally
        {
            Release(device);
            Release(adapter);
            Release(factory);
        }
    }

    /// <summary>
    /// Separates "this hardware cannot do D3D12" from "there is no D3D12 runtime" by retrying on WARP,
    /// which is a software implementation and therefore available whenever the runtime itself is.
    /// The distinction decides whether the user needs a driver, a GPU, or a Windows update.
    /// </summary>
    private static string DiagnoseNoDevice(nint factory, int hr)
    {
        nint warp = 0;
        bool warpWorks = false;
        try
        {
            Guid iid = IID_IDXGIAdapter1;
            void** vtbl = *(void***)factory;
            var fn = (delegate* unmanaged<nint, Guid*, nint*, int>)vtbl[VtEnumWarpAdapter];
            nint local = 0;
            if (fn(factory, &iid, &local) >= 0 && local != 0)
            {
                warp = local;
                if (D3D12CreateDevice(warp, FeatureLevel11_0, IID_ID3D12Device, out nint wd) >= 0)
                {
                    warpWorks = true;
                    Release(wd);
                }
            }
        }
        catch (DllNotFoundException) { /* no runtime; warpWorks stays false */ }
        finally { Release(warp); }

        return warpWorks
            ? $"the GPU does not support Direct3D 12 (hr=0x{hr:X8}); the D3D12 runtime itself is present, "
              + "so this is a hardware or driver limitation"
            : $"no Direct3D 12 runtime (hr=0x{hr:X8})";
    }
}
