namespace Aver.Launcher.Core;

/// <summary>What kind of thing an option turns on, which decides how the UI groups it.</summary>
public enum EngineOptionKind
{
    /// <summary>An optional feature module (<c>AVER_MODULE_*</c>).</summary>
    Module,

    /// <summary>A render backend (<c>AVER_RHI_*</c>).</summary>
    RhiBackend,

    /// <summary>The in-window editor UI.</summary>
    Editor,

    /// <summary>Build-only: sample app, tests. Not part of a shipped edition.</summary>
    Build,
}

/// <summary>
/// One engine build option, mirroring an <c>option()</c> line in the engine's root CMakeLists.
/// </summary>
/// <param name="Key">The CMake cache key, e.g. <c>AVER_MODULE_VOXI</c>.</param>
/// <param name="DisplayName">Short human label.</param>
/// <param name="Description">What it is, from the CMake help text.</param>
/// <param name="DefaultOn">The engine's default.</param>
/// <param name="Kind">Grouping.</param>
/// <param name="Requires">
/// Keys that must also be ON. The engine enforces these by silently forcing this option OFF, so the
/// launcher replicates them to explain the flip rather than let the user discover it in a build log.
/// </param>
/// <param name="Consequence">What is lost when this is OFF, for the UI to show beside the checkbox.</param>
public sealed record EngineOption(
    string Key,
    string DisplayName,
    string Description,
    bool DefaultOn,
    EngineOptionKind Kind,
    string[] Requires,
    string Consequence);

/// <summary>A single change the resolver made to a requested option set, and why.</summary>
public sealed record OptionAdjustment(string Key, bool Requested, bool Effective, string Reason);

/// <summary>The outcome of resolving a requested option set through the engine's own rules.</summary>
public sealed class OptionResolution
{
    /// <summary>What CMake would actually configure.</summary>
    public required IReadOnlyDictionary<string, bool> Effective { get; init; }

    /// <summary>Options the engine's dependency guards would force off.</summary>
    public required IReadOnlyList<OptionAdjustment> Adjustments { get; init; }

    /// <summary>Configurations that are valid but produce something the user probably does not want.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>True when nothing had to be forced.</summary>
    public bool IsExactlyAsRequested => Adjustments.Count == 0;
}

/// <summary>
/// The engine's build options, its dependency rules, and what an edition is made of.
/// </summary>
/// <remarks>
/// <para>
/// Modules are <b>compile-time CMake options built as STATIC libraries</b>. There is no runtime plugin
/// loader and no per-module manifest, so a module cannot be added to or removed from an installed
/// engine. That is why the launcher ships prebuilt EDITIONS: a named option set, built once, published
/// as its own payload. Anything that presents modules as independently installable is lying about the
/// engine.
/// </para>
/// <para>
/// The dependency rules matter more than they look. The engine does not FAIL on an invalid
/// combination; it prints a status line and forces the option off. So a UI offering free checkboxes
/// produces a build quietly different from the one the user asked for. <see cref="Resolve"/>
/// replicates both guards so the flip can be shown at the moment it is chosen.
/// </para>
/// </remarks>
public static class EngineOptions
{
    public const string Voxi = "AVER_MODULE_VOXI";
    public const string Pbr = "AVER_MODULE_PBR";
    public const string Scripting = "AVER_MODULE_SCRIPTING";
    public const string Scene = "AVER_MODULE_SCENE";
    public const string Framework = "AVER_MODULE_FRAMEWORK";
    public const string Physics = "AVER_MODULE_PHYSICS";
    public const string RhiD3D12 = "AVER_RHI_D3D12";
    public const string RhiD3D11 = "AVER_RHI_D3D11";
    public const string RhiVulkan = "AVER_RHI_VULKAN";
    public const string EnableUi = "AVER_ENABLE_UI";
    public const string BuildSandbox = "AVER_BUILD_SANDBOX";
    public const string BuildTests = "AVER_BUILD_TESTS";

    /// <summary>
    /// Every option a staged payload's <c>payload.json</c> is expected to record. Staging refuses when
    /// one is ABSENT from the CMake cache rather than reading it as OFF, because absent means the cache
    /// predates the option and the edition is unknowable.
    /// </summary>
    public static readonly string[] EditionDefining =
    [
        Voxi, Pbr, Scripting, Scene, Framework, Physics,
        RhiD3D12, RhiD3D11, RhiVulkan, EnableUi,
    ];

    /// <summary>
    /// Modules compiled unconditionally: they are not behind any option, so every edition has them.
    /// The UI lists them as "always included" rather than leaving the user to wonder.
    /// </summary>
    public static readonly string[] AlwaysIncluded =
    [
        "Core", "Platform", "Assets", "Formats", "RHI", "Runtime",
        "Audio (mixer)", "Audio (WASAPI device)", "UI", "Render.UI", "UI ABI",
        "Audio ABI", "Actor preview",
    ];

    public static IReadOnlyList<EngineOption> All { get; } =
    [
        new(Pbr, "PBR materials",
            "Material library, C ABI and the surface BRDF.",
            DefaultOn: true, EngineOptionKind.Module, [],
            "Surfaces render as flat colours; no material or texture system."),

        new(Voxi, "Voxi renderer",
            "Antialiasing, global illumination, ray tracing and path-tracing settings.",
            DefaultOn: true, EngineOptionKind.Module, [Pbr],
            "No global illumination, no cone-traced reflections and no ray-traced shadows."),

        new(Scene, "Scene",
            "Entity/component world: entities, transforms, hierarchy and the C ABI.",
            DefaultOn: true, EngineOptionKind.Module, [],
            "No entity world, so nothing can be spawned or placed at runtime."),

        new(Framework, "Gameplay framework",
            "Game instance, game mode, actors, pawns and the play lifecycle.",
            DefaultOn: true, EngineOptionKind.Module, [Scene],
            "No actors, pawns or Play mode."),

        new(Scripting, "C# scripting",
            "In-process .NET scripting host (hostfxr/CoreCLR).",
            DefaultOn: true, EngineOptionKind.Module, [],
            "C# gameplay scripts cannot be hosted; the editor still runs."),

        new(Physics, "Physics",
            "Jolt Physics: rigid bodies, character controller, queries and contact events.",
            DefaultOn: true, EngineOptionKind.Module, [],
            "No collision, gravity or character movement. Saves compiling 153 translation units."),

        new(RhiD3D12, "Direct3D 12",
            "The primary render backend.",
            DefaultOn: true, EngineOptionKind.RhiBackend, [],
            "The engine has no working render backend and will not start."),

        new(RhiD3D11, "Direct3D 11",
            "Secondary render backend.",
            DefaultOn: true, EngineOptionKind.RhiBackend, [],
            "Nothing: this backend is a compiling stub today."),

        new(RhiVulkan, "Vulkan",
            "Vulkan backend. Requires the LunarG Vulkan SDK at build time.",
            DefaultOn: false, EngineOptionKind.RhiBackend, [],
            "Nothing: this backend is a compiling stub today."),

        new(EnableUi, "Editor UI",
            "In-window editor UI (Dear ImGui).",
            DefaultOn: true, EngineOptionKind.Editor, [],
            "No editor: the build becomes a runtime with no authoring surface."),
    ];

    public static EngineOption? Find(string key)
        => All.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The engine's defaults, as a fresh CMake configure would produce them.</summary>
    public static Dictionary<string, bool> Defaults()
        => All.ToDictionary(o => o.Key, o => o.DefaultOn, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Applies the engine's own dependency guards to a requested option set.
    /// </summary>
    /// <remarks>
    /// The two guards are transcribed from the engine's root CMakeLists, including their order:
    /// <list type="number">
    ///   <item><description>
    ///     <c>AVER_MODULE_VOXI AND NOT AVER_MODULE_PBR</c> forces Voxi off -- "Voxi renders materials
    ///     and cannot be built without them".
    ///   </description></item>
    ///   <item><description>
    ///     <c>AVER_MODULE_FRAMEWORK AND NOT AVER_MODULE_SCENE</c> forces the framework off -- "the
    ///     framework stands on the scene".
    ///   </description></item>
    /// </list>
    /// Both are one-way: a guard turns a dependent OFF, and never turns a dependency ON. Resolving in
    /// the other direction would produce a build the engine cannot configure.
    /// <para>
    /// Order is preserved for a reason. Because a guard only ever clears, a single pass in declaration
    /// order is sufficient today; the requirement lists are still walked generically so a future
    /// three-deep chain resolves rather than silently half-resolving.
    /// </para>
    /// </remarks>
    public static OptionResolution Resolve(IReadOnlyDictionary<string, bool> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);

        var effective = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (EngineOption o in All)
        {
            effective[o.Key] = requested.TryGetValue(o.Key, out bool v) ? v : o.DefaultOn;
        }

        var adjustments = new List<OptionAdjustment>();

        // Iterate to a fixed point so a dependency chain longer than one link settles. Bounded by the
        // option count, since each pass can only ever clear a flag.
        for (int pass = 0; pass < All.Count; pass++)
        {
            bool changed = false;
            foreach (EngineOption o in All)
            {
                if (!effective[o.Key]) continue;
                foreach (string need in o.Requires)
                {
                    if (effective.TryGetValue(need, out bool on) && on) continue;

                    effective[o.Key] = false;
                    changed = true;
                    string needName = Find(need)?.DisplayName ?? need;
                    adjustments.Add(new OptionAdjustment(
                        o.Key,
                        Requested: requested.TryGetValue(o.Key, out bool r) ? r : o.DefaultOn,
                        Effective: false,
                        $"{o.DisplayName} requires {needName}, so CMake forces {o.Key}=OFF."));
                    break;
                }
            }
            if (!changed) break;
        }

        var warnings = new List<string>();

        // D3D12 is the only backend that actually renders; the other two are compiling stubs. A build
        // without it configures and links, then fails to create a device at run time.
        if (!effective[RhiD3D12])
        {
            warnings.Add(
                "Direct3D 12 is off. It is the only working render backend -- D3D11 and Vulkan are "
                + "compiling stubs -- so this build will not render anything.");
        }

        if (effective[RhiVulkan])
        {
            warnings.Add(
                "Vulkan is on. The backend is a stub, and building it needs the LunarG Vulkan SDK "
                + "present at configure time.");
        }

        if (!effective[EnableUi])
        {
            warnings.Add(
                "The editor UI is off. The result is a runtime, not something the launcher can open a "
                + "project in.");
        }

        // Scripting without the framework compiles, but the C# gameplay API has nothing to bind to.
        if (effective[Scripting] && !effective[Framework])
        {
            warnings.Add(
                "C# scripting is on without the gameplay framework. Scripts can be hosted, but there "
                + "are no actors, pawns or Play mode for them to drive.");
        }

        return new OptionResolution
        {
            Effective = effective,
            Adjustments = adjustments,
            Warnings = warnings,
        };
    }
}
