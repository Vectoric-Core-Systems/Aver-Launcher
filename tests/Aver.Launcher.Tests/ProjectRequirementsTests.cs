using Aver.Launcher.Core;
using Xunit;

namespace Aver.Launcher.Tests;

public class ProjectRequirementsTests
{
    private static ProjectDesc MakeProject(
        Action<string> populate, string contentRoot = "Content")
    {
        string dir = Path.Combine(Path.GetTempPath(), "aver-req-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, contentRoot, "Scripts"));
        populate(dir);
        return new ProjectDesc
        {
            Name = "T",
            Dir = dir,
            ContentRoot = contentRoot,
            ManifestPath = Path.Combine(dir, "T.ocproject"),
        };
    }

    private static void Script(string dir, string name, string body)
        => File.WriteAllText(Path.Combine(dir, "Content", "Scripts", name), body);

    private static EngineEdition Edition(params string[] on)
    {
        var e = new EngineEdition { Id = "test" };
        foreach (string k in EngineOptions.EditionDefining) e.Options[k] = false;
        e.Options[EngineOptions.EnableUi] = true;
        e.Options[EngineOptions.RhiD3D12] = true;
        foreach (string k in on) e.Options[k] = true;
        return e;
    }

    [Fact]
    public void A_script_using_physics_requires_physics()
    {
        // The SkyForge shape: an FPS character and dynamic crates. On an edition without physics this
        // opens, renders, and then does nothing on Play.
        ProjectDesc p = MakeProject(d => Script(d, "FpsGameMode.cs", """
            using Aver.Framework;
            namespace Demo;
            public sealed class FpsGameMode : AverGameMode {
                private Body _crate;
                public void Spawn() { _crate = Physics.AddDynamicBox(default, default, 12f); }
            }
            """));

        ProjectRequirements req = ProjectRequirements.Infer(p);
        Assert.Contains(EngineOptions.Physics, req.Modules);
        Assert.Contains(EngineOptions.Framework, req.Modules);
        Assert.Contains(EngineOptions.Scripting, req.Modules);

        Assert.False(req.SatisfiedBy(Edition(EngineOptions.Framework, EngineOptions.Scene, EngineOptions.Scripting)));
        Assert.True(req.SatisfiedBy(Edition(
            EngineOptions.Framework, EngineOptions.Scene, EngineOptions.Scripting,
            EngineOptions.Physics, EngineOptions.Pbr)));
    }

    [Fact]
    public void AverCharacter_counts_as_physics_even_though_it_is_not_in_Physics_cs()
    {
        // AverCharacter is a Jolt CharacterVirtual capsule, so it is dead without the physics module.
        ProjectDesc p = MakeProject(d => Script(d, "FpsCharacter.cs", """
            using Aver.Framework;
            namespace Demo;
            public sealed class FpsCharacter : AverCharacter { }
            """));
        Assert.Contains(EngineOptions.Physics, ProjectRequirements.Infer(p).Modules);
    }

    [Fact]
    public void A_script_with_no_physics_does_not_require_it()
    {
        ProjectDesc p = MakeProject(d => Script(d, "Hud.cs", """
            using Aver.Scripting;
            namespace Demo;
            public sealed class Hud { public void Draw() { Log.Info("hi"); } }
            """));

        ProjectRequirements req = ProjectRequirements.Infer(p);
        Assert.DoesNotContain(EngineOptions.Physics, req.Modules);
        Assert.Contains(EngineOptions.Scripting, req.Modules);
    }

    [Fact]
    public void Token_matching_does_not_fire_on_a_longer_identifier()
    {
        // "Body" must not match "BodyText" or "RigidBodyish"; that would demand physics of a HUD.
        ProjectDesc p = MakeProject(d => Script(d, "Hud.cs", """
            using Aver.Scripting;
            namespace Demo;
            public sealed class Hud { private string BodyText = ""; private int RigidBodyish; }
            """));
        Assert.DoesNotContain(EngineOptions.Physics, ProjectRequirements.Infer(p).Modules);
    }

    [Fact]
    public void A_level_requires_the_scene_and_materials_require_pbr()
    {
        ProjectDesc p = MakeProject(d =>
        {
            Directory.CreateDirectory(Path.Combine(d, "Content", "Maps"));
            Directory.CreateDirectory(Path.Combine(d, "Content", "Materials"));
            File.WriteAllText(Path.Combine(d, "Content", "Maps", "Default.ocworld"), "OCWORLD 1");
            File.WriteAllText(Path.Combine(d, "Content", "Materials", "M_Wall.ocmat"), "OCMAT 1");
        });

        ProjectRequirements req = ProjectRequirements.Infer(p);
        Assert.Contains(EngineOptions.Scene, req.Modules);
        Assert.Contains(EngineOptions.Pbr, req.Modules);
    }

    [Fact]
    public void Build_output_under_Scripts_is_not_treated_as_project_code()
    {
        // bin/obj hold copies of engine sources in some layouts; counting them would make every
        // project require everything.
        ProjectDesc p = MakeProject(d =>
        {
            string obj = Path.Combine(d, "Content", "Scripts", "obj", "Debug");
            Directory.CreateDirectory(obj);
            File.WriteAllText(Path.Combine(obj, "Generated.cs"), "using Aver.Framework; class X { Body b; }");
        });
        Assert.DoesNotContain(EngineOptions.Physics, ProjectRequirements.Infer(p).Modules);
    }

    [Fact]
    public void Every_requirement_carries_a_reason()
    {
        ProjectDesc p = MakeProject(d => Script(d, "G.cs", "using Aver.Framework;\nclass G { Body b; }"));
        Assert.All(ProjectRequirements.Infer(p).Evidence,
            e => Assert.False(string.IsNullOrWhiteSpace(e.Reason), $"{e.Module} has no reason"));
    }

    [Fact]
    public void Missing_modules_are_named_against_an_edition()
    {
        ProjectDesc p = MakeProject(d => Script(d, "G.cs", "using Aver.Framework;\nclass G { Body b; }"));
        ProjectRequirements req = ProjectRequirements.Infer(p);

        IReadOnlyList<RequirementEvidence> missing = req.MissingIn(
            Edition(EngineOptions.Framework, EngineOptions.Scene, EngineOptions.Scripting));

        RequirementEvidence physics = Assert.Single(missing, m => m.Module == EngineOptions.Physics);
        Assert.Equal("Physics", physics.ModuleName);
    }

    [Fact]
    public void An_empty_project_requires_nothing()
        => Assert.True(ProjectRequirements.Infer(MakeProject(_ => { })).IsEmpty);

    // ---------------------------------------------------------------- selection

    private static EngineInstall Install(string id, string version, params string[] modules)
    {
        string dir = Path.Combine(Path.GetTempPath(), "aver-sel-tests", Guid.NewGuid().ToString("N"), "bin");
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "Sandbox.exe");
        File.WriteAllText(exe, "x");
        EngineEdition ed = Edition(modules);
        ed.Id = id;
        return new EngineInstall
        {
            Root = Path.GetDirectoryName(dir)!,
            EntryPoint = exe,
            Version = version,
            Edition = ed,
        };
    }

    private static ProjectEntry EntryFor(ProjectDesc desc)
        => new() { ManifestPath = desc.ManifestPath, DisplayName = desc.Name, Desc = desc };

    [Fact]
    public void Capability_beats_version_when_choosing_an_engine()
    {
        // A NEWER engine without physics would open the project and silently break Play. An older one
        // that actually runs it is the better answer, so capability sorts above version.
        ProjectDesc p = MakeProject(d => Script(d, "G.cs", "using Aver.Framework;\nclass G { Body b; }"));
        EngineInstall newLean = Install("minimal", "0.2.0",
            EngineOptions.Framework, EngineOptions.Scene, EngineOptions.Scripting);
        EngineInstall oldFull = Install("standard", "0.1.0",
            EngineOptions.Framework, EngineOptions.Scene, EngineOptions.Scripting, EngineOptions.Physics);

        EngineChoice choice = EntryFor(p).Resolve([newLean, oldFull]);
        Assert.Same(oldFull, choice.Install);
        Assert.True(choice.FullySatisfied);
    }

    [Fact]
    public void When_nothing_satisfies_it_still_picks_one_and_names_the_shortfall()
    {
        // Refusing outright would leave a disabled button and no explanation. The user may well want
        // to open it anyway to look at the level.
        ProjectDesc p = MakeProject(d => Script(d, "G.cs", "using Aver.Framework;\nclass G { Body b; }"));
        EngineInstall lean = Install("minimal", "0.1.0",
            EngineOptions.Framework, EngineOptions.Scene, EngineOptions.Scripting);

        EngineChoice choice = EntryFor(p).Resolve([lean]);
        Assert.Same(lean, choice.Install);
        Assert.False(choice.FullySatisfied);
        Assert.Contains(choice.Missing, m => m.Module == EngineOptions.Physics);
    }
}
