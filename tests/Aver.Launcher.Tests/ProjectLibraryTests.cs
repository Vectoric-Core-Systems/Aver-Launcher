using Aver.Launcher.Core;
using Xunit;

namespace Aver.Launcher.Tests;

public class ProjectLibraryTests
{
    private static EngineInstall Install(string editionId, string version, params string[] modulesOn)
    {
        var ed = new EngineEdition { Id = editionId, DisplayName = editionId };
        foreach (string k in EngineOptions.EditionDefining) ed.Options[k] = false;
        // Every edition can at least open projects, so the tests isolate the module tie-break.
        ed.Options[EngineOptions.EnableUi] = true;
        ed.Options[EngineOptions.RhiD3D12] = true;
        foreach (string m in modulesOn) ed.Options[m] = true;

        string dir = Path.Combine(Path.GetTempPath(), "aver-install-tests", Guid.NewGuid().ToString("N"), "bin");
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "Sandbox.exe");
        File.WriteAllText(exe, "not a real exe");

        return new EngineInstall
        {
            Root = Path.GetDirectoryName(dir)!,
            EntryPoint = exe,
            Version = version,
            Edition = ed,
        };
    }

    private static ProjectEntry Project(string engineMin)
        => new()
        {
            ManifestPath = @"C:\P\Demo\Demo.ocproject",
            DisplayName = "Demo",
            Desc = new ProjectDesc { Name = "Demo", EngineName = "Aver", EngineMinVersion = engineMin },
        };

    [Fact]
    public void Prefers_the_edition_with_more_modules_over_the_alphabetically_first()
    {
        // The bug this pins: ordering by edition name picks "minimal" over "standard", so a project
        // opens in an engine without physics and behaves differently for no stated reason.
        EngineInstall minimal = Install("minimal", "0.1.0", EngineOptions.Pbr, EngineOptions.Scene);
        EngineInstall standard = Install("standard", "0.1.0",
            EngineOptions.Pbr, EngineOptions.Scene, EngineOptions.Physics, EngineOptions.Voxi);

        EngineInstall? best = Project("0.1.0").BestEngine([minimal, standard]);
        Assert.Same(standard, best);

        // Order of the candidate list must not decide it.
        Assert.Same(standard, Project("0.1.0").BestEngine([standard, minimal]));
    }

    [Fact]
    public void Version_beats_module_count()
    {
        // A newer engine is preferred even when leaner: the version floor is the project's own
        // declared requirement, and module count is only a tie-break within a version.
        EngineInstall oldFull = Install("standard", "0.1.0",
            EngineOptions.Pbr, EngineOptions.Scene, EngineOptions.Physics, EngineOptions.Voxi);
        EngineInstall newLean = Install("minimal", "0.2.0", EngineOptions.Pbr);

        Assert.Same(newLean, Project("0.1.0").BestEngine([oldFull, newLean]));
    }

    [Fact]
    public void An_engine_below_the_floor_is_never_chosen()
    {
        EngineInstall old = Install("standard", "0.1.0", EngineOptions.Pbr, EngineOptions.Physics);
        Assert.Null(Project("0.9.0").BestEngine([old]));
    }

    [Fact]
    public void An_edition_that_cannot_open_projects_is_never_chosen()
    {
        EngineInstall runtime = Install("runtime", "0.1.0", EngineOptions.Pbr);
        runtime.Edition.Options[EngineOptions.EnableUi] = false;   // no editor
        Assert.Null(Project("0.1.0").BestEngine([runtime]));

        EngineInstall noBackend = Install("nogpu", "0.1.0", EngineOptions.Pbr);
        noBackend.Edition.Options[EngineOptions.RhiD3D12] = false;
        Assert.Null(Project("0.1.0").BestEngine([noBackend]));
    }

    [Fact]
    public void A_project_that_failed_to_parse_resolves_to_no_engine()
    {
        var broken = new ProjectEntry
        {
            ManifestPath = @"C:\P\Broken\Broken.ocproject",
            DisplayName = "Broken",
            Desc = null,
            Error = "missing NAME",
        };
        Assert.Null(broken.BestEngine([Install("standard", "0.1.0", EngineOptions.Pbr)]));
    }

    [Fact]
    public void A_project_with_no_floor_opens_in_anything_capable()
    {
        EngineInstall any = Install("standard", "0.1.0", EngineOptions.Pbr);
        Assert.Same(any, Project(string.Empty).BestEngine([any]));
    }

    [Fact]
    public void Scan_root_looks_one_and_two_levels_deep_only()
    {
        string root = Path.Combine(Path.GetTempPath(), "aver-scan-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Alpha"));
        Directory.CreateDirectory(Path.Combine(root, "Beta", "Content", "Maps"));
        File.WriteAllText(Path.Combine(root, "Loose.ocproject"), "OCPROJECT 1\nNAME Loose\n");
        File.WriteAllText(Path.Combine(root, "Alpha", "Alpha.ocproject"), "OCPROJECT 1\nNAME Alpha\n");
        // Three levels down: deliberately NOT found. Descending a project's Content tree would make
        // startup cost scale with asset count, to find manifests convention puts at the top.
        File.WriteAllText(Path.Combine(root, "Beta", "Content", "Maps", "Deep.ocproject"), "OCPROJECT 1\nNAME Deep\n");

        IReadOnlyList<string> found = ProjectLibrary.ScanRoot(root);
        string[] names = found.Select(Path.GetFileNameWithoutExtension).OrderBy(n => n).ToArray()!;
        Assert.Equal(["Alpha", "Loose"], names);
    }

    [Fact]
    public void Scan_root_of_a_missing_directory_is_empty_not_an_error()
        => Assert.Empty(ProjectLibrary.ScanRoot(Path.Combine(Path.GetTempPath(), "aver-nope-" + Guid.NewGuid())));
}
