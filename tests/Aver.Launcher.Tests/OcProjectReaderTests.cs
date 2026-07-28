using Aver.Launcher.Core;
using Xunit;

namespace Aver.Launcher.Tests;

public class OcProjectReaderTests
{
    private static ProjectDesc Parse(string text)
    {
        var d = new ProjectDesc();
        Assert.True(OcProjectReader.TryParse(text, d, out string? err), err);
        return d;
    }

    // Verbatim copies of the manifests that exist on disk today, so the parser is tested against
    // real content without the suite depending on machine-specific paths. RealManifestsOnThisMachine
    // below reads the actual files when they are present.
    private const string SkyForgeManifest =
        "OCPROJECT 1\n" +
        "# Created by the Aver Engine editor. This project lives OUTSIDE the engine tree and\n" +
        "# references it; see the engine's docs/PROJECTS.md.\n" +
        "NAME SkyForge\n" +
        "ENGINE Aver 0.1.0\n" +
        "CONTENT Content\n" +
        "STARTMAP Maps/Default.ocworld\n" +
        "# AUTHOR <your name>\n";

    private const string OpenConstructorManifest =
        "OCPROJECT 1\n" +
        "NAME OpenConstructor\n" +
        "ENGINE Aver 0.1.0\n" +
        "CONTENT Content\n" +
        "STARTMAP Maps/demoworld.ocmap\n" +
        "AUTHOR OpenConstructor Team\n";

    [Fact]
    public void Parses_the_SkyForge_manifest()
    {
        ProjectDesc d = Parse(SkyForgeManifest);
        Assert.Equal(1, d.Version);
        Assert.Equal("SkyForge", d.Name);
        Assert.Equal("Aver", d.EngineName);
        Assert.Equal("0.1.0", d.EngineMinVersion);
        Assert.Equal("Content", d.ContentRoot);
        Assert.Equal("Maps/Default.ocworld", d.StartMap);
        // AUTHOR is commented out, so it must stay empty rather than picking up "<your name>".
        Assert.Equal(string.Empty, d.Author);
    }

    [Fact]
    public void Parses_the_OpenConstructor_manifest_with_a_multi_word_author()
    {
        ProjectDesc d = Parse(OpenConstructorManifest);
        Assert.Equal("OpenConstructor", d.Name);
        // AUTHOR is prose to end of line: splitting on whitespace would yield only "OpenConstructor".
        Assert.Equal("OpenConstructor Team", d.Author);
        Assert.Equal("Maps/demoworld.ocmap", d.StartMap);
    }

    [Fact]
    public void StartMap_may_name_a_file_that_does_not_exist_and_extension_is_not_assumed()
    {
        // The scaffold template writes Maps/Default.ocmap while SkyForge on disk says
        // Maps/Default.ocworld. The engine logs and carries on, so nothing here may assume .ocmap.
        Assert.Equal("Maps/Default.ocworld", Parse(SkyForgeManifest).StartMap);
        Assert.Equal("Maps/Default.ocmap",
            Parse("OCPROJECT 1\nNAME P\nSTARTMAP Maps/Default.ocmap\n").StartMap);
    }

    [Fact]
    public void Unknown_keys_are_ignored_and_recorded()
    {
        // docs/PROJECTS.md: "an unrecognised key is skipped silently" -- a guarantee the loader
        // keeps. A manifest written by a NEWER engine must still load here.
        ProjectDesc d = Parse(
            "OCPROJECT 1\n" +
            "NAME Future\n" +
            "ENGINE Aver 0.1.0\n" +
            "PLUGINS SoftBody Vehicle\n" +
            "COOKTARGET win64\n" +
            "ENGINEPIN Aver 0.4.2\n");

        Assert.Equal("Future", d.Name);
        Assert.Equal(new[] { "PLUGINS", "COOKTARGET", "ENGINEPIN" }, d.UnknownKeys);
    }

    [Fact]
    public void Strips_a_utf8_bom()
    {
        // Notepad and PowerShell's Out-File -Encoding utf8 both write one. Left in place it becomes
        // part of the first token and OCPROJECT stops matching.
        ProjectDesc d = Parse("\uFEFFOCPROJECT 1\nNAME Bommed\n");
        Assert.Equal("Bommed", d.Name);
    }

    [Fact]
    public void Handles_crlf_comments_and_a_trailing_semicolon()
    {
        ProjectDesc d = Parse(
            "OCPROJECT 1\r\n" +
            "NAME Windows Line Endings\r\n" +   // \r must be trimmed, not kept in the name
            "ENGINE Aver 0.1.0;\r\n" +          // one trailing ';' stripped
            "CONTENT Assets   # not Content\r\n");

        Assert.Equal("Windows Line Endings", d.Name);
        Assert.Equal("0.1.0", d.EngineMinVersion);
        Assert.Equal("Assets", d.ContentRoot);
    }

    [Fact]
    public void Keys_are_case_insensitive()
    {
        ProjectDesc d = Parse("ocproject 1\nname Lower\nengine aver 0.1.0\nstartmap Maps/A.ocworld\n");
        Assert.Equal("Lower", d.Name);
        Assert.Equal("aver", d.EngineName);   // value keeps its case; only the KEY is folded
        Assert.True(AverVersion.IsEngineNameMatch(d.EngineName));
    }

    [Fact]
    public void Content_root_defaults_when_absent()
        => Assert.Equal("Content", Parse("OCPROJECT 1\nNAME NoContentKey\n").ContentRoot);

    [Theory]
    [InlineData("NAME Orphan\n", "not an .ocproject: no OCPROJECT header line")]
    [InlineData("OCPROJECT 1\nENGINE Aver 0.1.0\n", "missing NAME")]
    [InlineData("", "not an .ocproject: no OCPROJECT header line")]
    public void The_only_two_hard_errors(string text, string expected)
    {
        var d = new ProjectDesc();
        Assert.False(OcProjectReader.TryParse(text, d, out string? err));
        Assert.Equal(expected, err);
    }

    [Fact]
    public void Version_parses_a_leading_integer_like_from_chars_does()
    {
        // std::from_chars succeeds on a prefix, so "1abc" is 1 -- not the fallback.
        Assert.Equal(1, Parse("OCPROJECT 1abc\nNAME P\n").Version);
        Assert.Equal(2, Parse("OCPROJECT 2\nNAME P\n").Version);
        // No leading digits at all falls back to 1.
        Assert.Equal(1, Parse("OCPROJECT vNext\nNAME P\n").Version);
        // A bare header with no token keeps the default.
        Assert.Equal(1, Parse("OCPROJECT\nNAME P\n").Version);
    }

    [Fact]
    public void Derived_paths_follow_the_engine_layout()
    {
        var d = new ProjectDesc { Dir = @"C:\P\Demo", ContentRoot = "Content" };
        Assert.Equal(@"C:\P\Demo\Content", d.ContentDir);
        Assert.Equal(@"C:\P\Demo\Content\Scripts", d.ScriptsDir);
        // Binaries sits OUTSIDE Content: build output is neither content nor something to ship.
        Assert.Equal(@"C:\P\Demo\Binaries", d.BinariesDir);
    }

    // ---------------------------------------------------------------- the engine-compatibility gate

    [Fact]
    public void Load_refuses_a_foreign_engine_name()
    {
        string p = WriteTemp("OCPROJECT 1\nNAME Foreign\nENGINE Unreal 5.4\n");
        Assert.False(OcProjectReader.TryLoad(p, "0.1.0", out _, out string? err));
        Assert.Contains("targets engine 'Unreal'", err);
    }

    [Fact]
    public void Load_refuses_a_project_needing_a_newer_build()
    {
        string p = WriteTemp("OCPROJECT 1\nNAME FromTheFuture\nENGINE Aver 0.9.0\n");
        Assert.False(OcProjectReader.TryLoad(p, "0.1.0", out _, out string? err));
        Assert.Contains("needs Aver 0.9.0 or newer", err);

        // ...and accepts it once a satisfying build exists.
        Assert.True(OcProjectReader.TryLoad(p, "1.0.0", out _, out err), err);
    }

    [Fact]
    public void Load_without_an_engine_version_parses_without_gating()
    {
        // The project library must LIST a project it cannot currently open, and say why, rather than
        // pretend it is absent. Passing null skips the version gate but keeps the name gate.
        string p = WriteTemp("OCPROJECT 1\nNAME FromTheFuture\nENGINE Aver 9.9.9\n");
        Assert.True(OcProjectReader.TryLoad(p, engineVersion: null, out ProjectDesc? d, out string? err), err);
        Assert.Equal("9.9.9", d!.EngineMinVersion);
        Assert.False(AverVersion.Satisfies(d.EngineMinVersion, "0.1.0"));
    }

    [Fact]
    public void Load_sets_absolute_dir_and_manifest_path()
    {
        string p = WriteTemp("OCPROJECT 1\nNAME Pathy\n");
        Assert.True(OcProjectReader.TryLoad(p, "0.1.0", out ProjectDesc? d, out string? err), err);
        Assert.Equal(Path.GetFullPath(p), d!.ManifestPath);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(p)), d.Dir);
    }

    [Fact]
    public void Load_reports_a_missing_file_rather_than_throwing()
    {
        string p = Path.Combine(Path.GetTempPath(), "aver-does-not-exist-" + Guid.NewGuid() + ".ocproject");
        Assert.False(OcProjectReader.TryLoad(p, "0.1.0", out _, out string? err));
        Assert.Contains("cannot read file", err);
    }

    /// <summary>
    /// Reads the manifests that actually exist on this machine, when they do. Not skipped as
    /// "inconclusive" on other machines -- it simply has nothing to assert there, and the inline
    /// fixtures above carry the real coverage.
    /// </summary>
    [Fact]
    public void Real_manifests_on_this_machine_parse()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Aver Projects");
        if (!Directory.Exists(root)) return;

        string[] found = Directory.GetFiles(root, "*.ocproject", SearchOption.AllDirectories);

        // A projects root that exists but holds no manifest means the layout moved, not that there
        // is nothing to check. Without this the test passes while asserting nothing -- the exact
        // shape of "verified" claim this codebase has been bitten by before.
        Assert.NotEmpty(found);

        foreach (string f in found)
        {
            Assert.True(
                OcProjectReader.TryLoad(f, engineVersion: null, out ProjectDesc? d, out string? err),
                $"{f}: {err}");
            Assert.False(string.IsNullOrWhiteSpace(d!.Name), $"{f} parsed with an empty NAME");
        }
    }

    private static string WriteTemp(string content)
    {
        string dir = Path.Combine(Path.GetTempPath(), "aver-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, "Test.ocproject");
        File.WriteAllText(p, content);
        return p;
    }
}
