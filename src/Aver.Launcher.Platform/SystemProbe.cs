using System.Runtime.InteropServices;
using Aver.Launcher.Core;
using Microsoft.Win32;

namespace Aver.Launcher.Platform;

/// <summary>
/// Windows version, Visual C++ runtime and .NET presence.
/// </summary>
public static unsafe partial class SystemProbe
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string VcRuntimeKey = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64";

    [LibraryImport("ntdll.dll")]
    private static partial int RtlGetVersion(ref OsVersionInfoEx versionInformation);

    // Blittable on purpose: a [MarshalAs(ByValTStr)] string field makes the struct unsupported by
    // source-generated P/Invoke (SYSLIB1051). CSDVersion is unused here anyway.
    [StructLayout(LayoutKind.Sequential)]
    private struct OsVersionInfoEx
    {
        public uint OSVersionInfoSize;
        public uint MajorVersion;
        public uint MinorVersion;
        public uint BuildNumber;
        public uint PlatformId;
        public fixed char CSDVersion[128];
        public ushort ServicePackMajor;
        public ushort ServicePackMinor;
        public ushort SuiteMask;
        public byte ProductType;
        public byte Reserved;
    }

    /// <summary>
    /// Reads the Windows build number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never read <c>ProductName</c>.</b> On this Windows 11 25H2 machine it says
    /// "Windows 10 Pro" -- Microsoft never updated it, so anything branching on it is already wrong.
    /// <b>Never read <c>ReleaseId</c></b> either: it is frozen at "2009". The values that are
    /// maintained are <c>CurrentBuild</c>, <c>UBR</c> and <c>DisplayVersion</c>.
    /// </para>
    /// <para>
    /// <c>RtlGetVersion</c> is consulted as a cross-check because, unlike <c>GetVersionEx</c>, it is
    /// not subject to manifest-based version shimming. A disagreement with the registry is recorded
    /// rather than silently resolved -- the registry wins, since that is what the OS servicing stack
    /// updates, but a mismatch is a signal something is lying.
    /// </para>
    /// </remarks>
    public static OsInfo QueryOs(out string? warning)
    {
        warning = null;
        var info = new OsInfo();

        using RegistryKey? key = RegistryKey
            .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(CurrentVersionKey);

        if (key is not null)
        {
            // CurrentBuild is REG_SZ, not a DWORD. CurrentBuildNumber is the same value; UBR is a DWORD.
            if (key.GetValue("CurrentBuild") is string cb && int.TryParse(cb, out int build))
                info.Build = build;
            if (key.GetValue("UBR") is int ubr)
                info.UpdateBuildRevision = ubr;
            if (key.GetValue("DisplayVersion") is string dv)
                info.DisplayVersion = dv;
        }

        var vi = new OsVersionInfoEx { OSVersionInfoSize = (uint)Marshal.SizeOf<OsVersionInfoEx>() };
        if (RtlGetVersion(ref vi) == 0 && vi.BuildNumber != 0)
        {
            if (info.Build == 0)
            {
                info.Build = (int)vi.BuildNumber;
            }
            else if (info.Build != (int)vi.BuildNumber)
            {
                warning = $"registry reports build {info.Build} but RtlGetVersion reports "
                          + $"{vi.BuildNumber}; using the registry";
            }
        }

        return info;
    }

    /// <summary>
    /// Reports the Visual C++ runtime, by registry AND by trying to load the DLLs.
    /// </summary>
    /// <remarks>
    /// The functional check is the one that matters. The engine needs <c>VCRUNTIME140_1.dll</c>, which
    /// exists only in VS2019 16.0+ redistributables, so a machine carrying only the VC++ 2015 redist
    /// satisfies a naive "14.x installed" registry test and then fails at process start with a missing
    /// DLL dialog. <c>LOAD_LIBRARY_AS_DATAFILE</c> answers "is it there and loadable" without running
    /// any initialisation code from it.
    /// <para>
    /// Deliberately NOT checked: <c>api-ms-win-crt-*</c>. Those are Universal CRT imports, part of
    /// Windows 10 and later, and not supplied by the redistributable.
    /// </para>
    /// </remarks>
    public static VcRedistInfo QueryVcRedist()
    {
        var info = new VcRedistInfo();

        using RegistryKey? key = RegistryKey
            .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(VcRuntimeKey);

        if (key is not null)
        {
            info.Installed = key.GetValue("Installed") is int i && i == 1;
            if (key.GetValue("Major") is int major) info.Major = major;
            if (key.GetValue("Minor") is int minor) info.Minor = minor;
            if (key.GetValue("Bld") is int bld) info.Build = bld;
            if (key.GetValue("Version") is string v) info.Version = v;
        }

        foreach (string dll in new[] { "MSVCP140.dll", "VCRUNTIME140.dll", "VCRUNTIME140_1.dll" })
        {
            nint h = LoadLibraryEx(dll, 0, LoadLibraryAsDatafile);
            if (h != 0)
            {
                info.LoadableDlls.Add(dll);
                FreeLibrary(h);
            }
        }

        return info;
    }

    private const uint LoadLibraryAsDatafile = 0x00000002;

    // EntryPoint is explicit because [LibraryImport] does not append the A/W suffix the way
    // [DllImport] with ExactSpelling=false did; "LoadLibraryEx" is not an exported name.
    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint LoadLibraryEx(string lpLibFileName, nint hFile, uint dwFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(nint hModule);

    /// <summary>
    /// Finds .NET runtimes and SDKs by looking at the filesystem.
    /// </summary>
    /// <remarks>
    /// Not by shelling <c>dotnet --list-runtimes</c>: <c>dotnet</c> may not be on PATH even when a
    /// runtime is installed, and spawning a process to read a directory listing is slower and can fail
    /// for reasons unrelated to the question. The root is resolved the way the host itself does --
    /// registry, then <c>DOTNET_ROOT</c>, then the default location.
    /// </remarks>
    public static DotnetInfo QueryDotnet()
    {
        var info = new DotnetInfo();

        string? root = null;
        using (RegistryKey? key = RegistryKey
                   .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                   .OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64"))
        {
            if (key?.GetValue("InstallLocation") is string loc && loc.Length > 0) root = loc;
        }

        root ??= Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            root = Path.Combine(pf, "dotnet");
        }

        if (!Directory.Exists(root)) return info;
        info.Root = root;

        // The bridge needs Microsoft.NETCore.App specifically. WindowsDesktop is NOT required by the
        // engine -- only by this launcher, which ships self-contained and so carries its own.
        CollectVersions(Path.Combine(root, "shared", "Microsoft.NETCore.App"), info.Runtimes);
        CollectVersions(Path.Combine(root, "sdk"), info.Sdks);
        return info;
    }

    private static void CollectVersions(string dir, List<string> into)
    {
        if (!Directory.Exists(dir)) return;
        foreach (string sub in Directory.GetDirectories(dir))
        {
            string name = Path.GetFileName(sub);
            // Skip the SDK's NuGetFallbackFolder and anything else that is not a version.
            if (name.Length > 0 && char.IsAsciiDigit(name[0])) into.Add(name);
        }

        into.Sort((a, b) => AverVersion.Compare(b, a));   // newest first
    }

    /// <summary>Runs every probe and assembles a report.</summary>
    public static PrereqReport QueryAll(out string? warning)
    {
        OsInfo os = QueryOs(out warning);
        return new PrereqReport
        {
            Os = os,
            VcRedist = QueryVcRedist(),
            Dotnet = QueryDotnet(),
            Caps = D3D12Probe.Query(),
        };
    }
}
