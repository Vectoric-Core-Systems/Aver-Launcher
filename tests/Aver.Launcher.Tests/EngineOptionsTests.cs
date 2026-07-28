using Aver.Launcher.Core;
using Xunit;

namespace Aver.Launcher.Tests;

public class EngineOptionsTests
{
    private static Dictionary<string, bool> Req(params (string Key, bool On)[] overrides)
    {
        Dictionary<string, bool> d = EngineOptions.Defaults();
        foreach ((string k, bool v) in overrides) d[k] = v;
        return d;
    }

    [Fact]
    public void Defaults_match_the_engine_CMakeLists()
    {
        Dictionary<string, bool> d = EngineOptions.Defaults();
        // Everything ships ON except Vulkan, which needs an SDK that is not installed.
        Assert.True(d[EngineOptions.Voxi]);
        Assert.True(d[EngineOptions.Pbr]);
        Assert.True(d[EngineOptions.Scripting]);
        Assert.True(d[EngineOptions.Scene]);
        Assert.True(d[EngineOptions.Framework]);
        Assert.True(d[EngineOptions.Physics]);
        Assert.True(d[EngineOptions.RhiD3D12]);
        Assert.True(d[EngineOptions.RhiD3D11]);
        Assert.True(d[EngineOptions.EnableUi]);
        Assert.False(d[EngineOptions.RhiVulkan]);
    }

    [Fact]
    public void Defaults_resolve_with_no_adjustments_and_no_warnings()
    {
        OptionResolution r = EngineOptions.Resolve(EngineOptions.Defaults());
        Assert.True(r.IsExactlyAsRequested);
        Assert.Empty(r.Warnings);
    }

    // ---------------------------------------------------------------- the two silent guards

    [Fact]
    public void Voxi_without_PBR_is_forced_off()
    {
        // CMake prints a status line and clears the flag; it does not fail. A UI that offered this
        // combination without saying so would hand the user a build with no GI and no explanation.
        OptionResolution r = EngineOptions.Resolve(Req((EngineOptions.Pbr, false)));

        Assert.False(r.Effective[EngineOptions.Voxi]);
        Assert.False(r.IsExactlyAsRequested);
        OptionAdjustment adj = Assert.Single(r.Adjustments);
        Assert.Equal(EngineOptions.Voxi, adj.Key);
        Assert.True(adj.Requested);
        Assert.False(adj.Effective);
        Assert.Contains("requires PBR materials", adj.Reason);
    }

    [Fact]
    public void Framework_without_Scene_is_forced_off()
    {
        OptionResolution r = EngineOptions.Resolve(Req((EngineOptions.Scene, false)));

        Assert.False(r.Effective[EngineOptions.Framework]);
        OptionAdjustment adj = Assert.Single(r.Adjustments, a => a.Key == EngineOptions.Framework);
        Assert.Contains("requires Scene", adj.Reason);
    }

    [Fact]
    public void A_guard_never_turns_a_dependency_on()
    {
        // Resolving "I want Voxi" into "so I will enable PBR for you" would produce a build the user
        // did not ask for. The engine's guards only ever clear, and so does this.
        OptionResolution r = EngineOptions.Resolve(Req(
            (EngineOptions.Pbr, false), (EngineOptions.Voxi, true)));
        Assert.False(r.Effective[EngineOptions.Pbr]);
        Assert.False(r.Effective[EngineOptions.Voxi]);
    }

    [Fact]
    public void Voxi_off_with_PBR_off_needs_no_adjustment()
    {
        OptionResolution r = EngineOptions.Resolve(Req(
            (EngineOptions.Pbr, false), (EngineOptions.Voxi, false)));
        Assert.True(r.IsExactlyAsRequested);
    }

    [Fact]
    public void Both_guards_can_fire_at_once()
    {
        OptionResolution r = EngineOptions.Resolve(Req(
            (EngineOptions.Pbr, false), (EngineOptions.Scene, false)));
        Assert.Equal(2, r.Adjustments.Count);
        Assert.False(r.Effective[EngineOptions.Voxi]);
        Assert.False(r.Effective[EngineOptions.Framework]);
    }

    [Fact]
    public void Resolution_is_idempotent()
    {
        // Feeding the effective set back in must change nothing, or the UI would flicker between two
        // states as the user toggles unrelated boxes.
        OptionResolution first = EngineOptions.Resolve(Req(
            (EngineOptions.Pbr, false), (EngineOptions.Scene, false)));
        OptionResolution second = EngineOptions.Resolve(first.Effective);

        Assert.True(second.IsExactlyAsRequested);
        Assert.Equal(first.Effective.OrderBy(k => k.Key), second.Effective.OrderBy(k => k.Key));
    }

    [Fact]
    public void An_unspecified_option_takes_the_engine_default()
    {
        OptionResolution r = EngineOptions.Resolve(new Dictionary<string, bool>());
        Assert.True(r.Effective[EngineOptions.Physics]);
        Assert.False(r.Effective[EngineOptions.RhiVulkan]);
    }

    // ---------------------------------------------------------------- warnings

    [Fact]
    public void Turning_off_D3D12_warns_that_nothing_will_render()
    {
        OptionResolution r = EngineOptions.Resolve(Req((EngineOptions.RhiD3D12, false)));
        Assert.Contains(r.Warnings, w => w.Contains("only working render backend"));
        // It is not an adjustment: CMake permits it, so the launcher must warn rather than override.
        Assert.True(r.IsExactlyAsRequested);
    }

    [Fact]
    public void Enabling_Vulkan_warns_that_it_is_a_stub_needing_an_SDK()
    {
        OptionResolution r = EngineOptions.Resolve(Req((EngineOptions.RhiVulkan, true)));
        Assert.Contains(r.Warnings, w => w.Contains("stub") && w.Contains("LunarG"));
    }

    [Fact]
    public void Scripting_without_the_framework_warns_it_has_nothing_to_drive()
    {
        OptionResolution r = EngineOptions.Resolve(Req((EngineOptions.Scene, false)));
        Assert.Contains(r.Warnings, w => w.Contains("no actors, pawns or Play mode"));
    }

    [Fact]
    public void Every_option_declares_a_consequence()
    {
        Assert.All(EngineOptions.All,
            o => Assert.False(string.IsNullOrWhiteSpace(o.Consequence), $"{o.Key} has no consequence"));
    }

    [Fact]
    public void Every_requirement_names_a_real_option()
    {
        foreach (EngineOption o in EngineOptions.All)
        {
            foreach (string need in o.Requires)
            {
                Assert.NotNull(EngineOptions.Find(need));
            }
        }
    }

    // ---------------------------------------------------------------- editions

    [Fact]
    public void The_standard_edition_describes_itself()
    {
        var e = new EngineEdition { Id = "standard", DisplayName = "Aver Engine (Standard)" };
        foreach (KeyValuePair<string, bool> kv in EngineOptions.Defaults()) e.Options[kv.Key] = kv.Value;

        Assert.True(e.IsComplete(out IReadOnlyList<string> missing), string.Join(", ", missing));
        Assert.True(e.CanEditProjects);
        Assert.Contains("Physics", e.IncludedModules);
        Assert.Contains("Voxi renderer", e.IncludedModules);
        Assert.Empty(e.ExcludedModules);
        Assert.Contains("Direct3D 12", e.Backends);
        Assert.DoesNotContain("Vulkan", e.Backends);
    }

    [Fact]
    public void A_minimal_edition_reports_what_it_gave_up()
    {
        var e = new EngineEdition { Id = "minimal" };
        foreach (KeyValuePair<string, bool> kv in EngineOptions.Resolve(Req(
                     (EngineOptions.Physics, false),
                     (EngineOptions.Voxi, false))).Effective)
        {
            e.Options[kv.Key] = kv.Value;
        }

        Assert.True(e.CanEditProjects);   // still an editor, just a leaner one
        Assert.DoesNotContain("Physics", e.IncludedModules);
        var excluded = e.ExcludedModules.ToDictionary(x => x.Module, x => x.Consequence);
        Assert.Contains("Physics", excluded.Keys);
        Assert.Contains("Nothing collides", excluded["Physics"]);
        Assert.Contains("global illumination", excluded["Voxi renderer"]);
    }

    [Fact]
    public void A_missing_option_is_reported_as_unknown_not_as_off()
    {
        // This is the real build-release failure: its CMakeCache lacked AVER_MODULE_PHYSICS, SCENE and
        // FRAMEWORK entirely, so reading absent as OFF would have advertised an edition that never
        // existed.
        var e = new EngineEdition { Id = "stale" };
        e.Options[EngineOptions.Pbr] = true;
        e.Options[EngineOptions.Voxi] = true;
        e.Options[EngineOptions.Scripting] = true;
        e.Options[EngineOptions.RhiD3D12] = true;
        e.Options[EngineOptions.RhiD3D11] = true;
        e.Options[EngineOptions.RhiVulkan] = false;
        e.Options[EngineOptions.EnableUi] = true;

        Assert.False(e.IsComplete(out IReadOnlyList<string> missing));
        Assert.Equal(
            [EngineOptions.Scene, EngineOptions.Framework, EngineOptions.Physics],
            missing.OrderBy(m => Array.IndexOf(EngineOptions.EditionDefining, m)));
    }

    [Fact]
    public void An_edition_with_no_editor_cannot_open_projects()
    {
        var e = new EngineEdition { Id = "runtime" };
        foreach (KeyValuePair<string, bool> kv in Req((EngineOptions.EnableUi, false))) e.Options[kv.Key] = kv.Value;
        Assert.False(e.CanEditProjects);
    }

    [Theory]
    [InlineData(0, "no optional modules")]
    [InlineData(2, "PBR materials, Voxi renderer")]
    public void Module_summary_stays_short(int count, string expected)
    {
        var e = new EngineEdition { Id = "x" };
        foreach (string k in EngineOptions.EditionDefining) e.Options[k] = false;
        string[] keys = [EngineOptions.Pbr, EngineOptions.Voxi];
        for (int i = 0; i < count; i++) e.Options[keys[i]] = true;
        Assert.Equal(expected, e.ModuleSummary);
    }

    [Fact]
    public void Module_summary_elides_past_three()
    {
        var e = new EngineEdition { Id = "x" };
        foreach (KeyValuePair<string, bool> kv in EngineOptions.Defaults()) e.Options[kv.Key] = kv.Value;
        Assert.EndsWith("more", e.ModuleSummary);
    }
}
