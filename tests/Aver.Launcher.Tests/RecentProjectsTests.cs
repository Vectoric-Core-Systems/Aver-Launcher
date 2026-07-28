using Aver.Launcher.Core;
using Xunit;

namespace Aver.Launcher.Tests;

public class RecentProjectsTests
{
    // Every path "exists" unless it contains "gone".
    private static readonly Func<string, bool> Exists = p => !p.Contains("gone", StringComparison.Ordinal);

    [Fact]
    public void Reads_paths_most_recent_first()
    {
        IReadOnlyList<string> r = RecentProjects.Parse(
            "C:\\P\\A\\A.ocproject\nC:\\P\\B\\B.ocproject\n", Exists);
        Assert.Equal(["C:\\P\\A\\A.ocproject", "C:\\P\\B\\B.ocproject"], r);
    }

    [Fact]
    public void Drops_entries_whose_file_has_gone()
    {
        // The list on this machine has held dead rows pointing at deleted scratch directories, so
        // this is the normal case rather than an edge one.
        IReadOnlyList<string> r = RecentProjects.Parse(
            "C:\\P\\A\\A.ocproject\nC:\\gone\\X.ocproject\nC:\\P\\B\\B.ocproject\n", Exists);
        Assert.Equal(["C:\\P\\A\\A.ocproject", "C:\\P\\B\\B.ocproject"], r);
    }

    [Fact]
    public void Trims_only_trailing_cr_and_space()
    {
        // The engine pops '\r' and ' ' off the end and nothing else. A leading space or a trailing
        // tab therefore makes a path the editor would reject -- and the launcher must reject it too,
        // or it lists a project the editor's own browser will not.
        IReadOnlyList<string> r = RecentProjects.Parse(
            "C:\\P\\A\\A.ocproject  \r\n" +      // trailing spaces + CR: accepted
            " C:\\P\\B\\B.ocproject\n" +          // LEADING space: not trimmed, so path differs
            "C:\\P\\C\\C.ocproject\t\n",          // trailing TAB: not trimmed, so path differs
            p => p is "C:\\P\\A\\A.ocproject" or "C:\\P\\B\\B.ocproject" or "C:\\P\\C\\C.ocproject");

        Assert.Equal(["C:\\P\\A\\A.ocproject"], r);
    }

    [Fact]
    public void Caps_at_ten_surviving_entries()
    {
        // The cap is applied WHILE reading, so an eleventh line is never even probed.
        string text = string.Concat(Enumerable.Range(0, 25).Select(i => $"C:\\P\\{i}.ocproject\n"));
        IReadOnlyList<string> r = RecentProjects.Parse(text, Exists);
        Assert.Equal(RecentProjects.MaxRecents, r.Count);
        Assert.Equal("C:\\P\\0.ocproject", r[0]);
        Assert.Equal("C:\\P\\9.ocproject", r[9]);
    }

    [Fact]
    public void Dead_entries_do_not_consume_cap_slots()
    {
        // Ten dead rows followed by two live ones must yield the two live ones: the cap counts what
        // survived, not what was read.
        string text = string.Concat(Enumerable.Range(0, 10).Select(i => $"C:\\gone\\{i}.ocproject\n"))
                    + "C:\\P\\A\\A.ocproject\nC:\\P\\B\\B.ocproject\n";
        IReadOnlyList<string> r = RecentProjects.Parse(text, Exists);
        Assert.Equal(["C:\\P\\A\\A.ocproject", "C:\\P\\B\\B.ocproject"], r);
    }

    [Fact]
    public void Tolerates_empty_blank_lines_and_no_trailing_newline()
    {
        Assert.Empty(RecentProjects.Parse("", Exists));
        Assert.Empty(RecentProjects.Parse("\n\n\n", Exists));
        Assert.Equal(["C:\\P\\A\\A.ocproject"],
            RecentProjects.Parse("\nC:\\P\\A\\A.ocproject", Exists));
    }

    [Fact]
    public void Missing_file_is_the_normal_first_run_state()
    {
        string p = Path.Combine(Path.GetTempPath(), "aver-no-recents-" + Guid.NewGuid() + ".txt");
        Assert.Empty(RecentProjects.Read(p, Exists));
    }

    [Fact]
    public void Display_name_is_the_manifest_stem_not_the_NAME_key()
    {
        Assert.Equal("SkyForge", RecentProjects.DisplayName(@"C:\P\SkyForge\SkyForge.ocproject"));
        // A renamed file shows the new stem even though the NAME key inside is unchanged.
        Assert.Equal("Renamed", RecentProjects.DisplayName(@"C:\P\SkyForge\Renamed.ocproject"));
    }

    [Fact]
    public void Well_known_paths_match_the_engine()
    {
        Assert.EndsWith(@"AverEngine", RecentProjects.UserDataDir);
        Assert.EndsWith(@"AverEngine\recent.txt", RecentProjects.RecentsPath);
        Assert.EndsWith(@"Aver Projects", RecentProjects.DefaultProjectsRoot);
        // Documents comes from the known folder, so a OneDrive-redirected Documents is honoured
        // rather than a %USERPROFILE%\Documents guess.
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            RecentProjects.DefaultProjectsRoot);
    }
}
