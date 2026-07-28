using Aver.Launcher.Core;
using Xunit;

namespace Aver.Launcher.Tests;

/// <summary>
/// The comparison table is transcribed from the CONTRACT stated in the engine's
/// <c>modules/core/include/aver/core/Version.hpp</c>, not from what this port happens to do. Each
/// case below is a property the engine's <c>compareVersions</c> has, so a divergence fails here
/// rather than in the field as "the launcher offered me an engine that then refused my project".
/// </summary>
public class AverVersionTests
{
    [Theory]
    // --- the documented headline case: missing trailing components read as 0 ---
    [InlineData("0.1", "0.1.0", 0)]
    [InlineData("0.1.0", "0.1", 0)]
    [InlineData("1", "1.0.0.0", 0)]
    [InlineData("", "0", 0)]
    [InlineData("", "", 0)]
    [InlineData("0.0.0", "", 0)]
    // --- ordinary ordering ---
    [InlineData("0.1.0", "0.2.0", -1)]
    [InlineData("0.2.0", "0.1.0", 1)]
    [InlineData("0.1.0", "1.0.0", -1)]
    [InlineData("1.0.0", "0.9.9", 1)]
    [InlineData("0.1.9", "0.1.10", -1)]
    // Numeric, not lexicographic. "0.1.9" > "0.1.10" as strings; the engine compares integers.
    [InlineData("0.9.0", "0.10.0", -1)]
    [InlineData("2.0.0", "10.0.0", -1)]
    // --- leading zeros are numerically irrelevant ---
    [InlineData("0.01.0", "0.1.0", 0)]
    [InlineData("00.1", "0.1", 0)]
    // --- junk reads as 0 rather than throwing, and trailing text is walked past ---
    [InlineData("1.2-beta", "1.2", 0)]
    [InlineData("abc", "0", 0)]
    [InlineData("abc", "0.0.0", 0)]
    [InlineData("1.x.3", "1.0.3", 0)]
    [InlineData("v1.0", "0.1.0", -1)]      // 'v' is not a digit, so the first component reads 0
    public void Compare_matches_the_engine_contract(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(AverVersion.Compare(a, b)));
        // Antisymmetry is a property of strcmp-like comparators and is worth asserting for free.
        Assert.Equal(-expected, Math.Sign(AverVersion.Compare(b, a)));
    }

    [Fact]
    public void Compare_disagrees_with_System_Version_exactly_where_documented()
    {
        // This test exists to justify the reimplementation. If it ever fails because
        // System.Version changed, the reimplementation is still correct -- the engine is the oracle.
        Assert.Equal(0, AverVersion.Compare("0.1", "0.1.0"));
        Assert.True(System.Version.Parse("0.1") < System.Version.Parse("0.1.0"));
    }

    [Theory]
    // The gate is: fail only when compareVersions(minVersion, current) > 0.
    [InlineData("0.1.0", "0.1.0", true)]    // exactly the floor
    [InlineData("0.1.0", "0.2.0", true)]    // newer engine opens an older project
    [InlineData("0.2.0", "0.1.0", false)]   // project needs newer than we have
    [InlineData("0.1", "0.1.0", true)]      // the equality case that System.Version gets wrong
    [InlineData("", "0.1.0", true)]         // no floor declared
    [InlineData(null, "0.1.0", true)]
    public void Satisfies_is_a_floor_not_a_pin(string? min, string engine, bool expected)
        => Assert.Equal(expected, AverVersion.Satisfies(min, engine));

    [Theory]
    [InlineData("Aver", true)]
    [InlineData("aver", true)]      // equalsCI
    [InlineData("AVER", true)]
    [InlineData("", true)]          // key is optional
    [InlineData(null, true)]
    [InlineData("Unreal", false)]
    [InlineData("Aver2", false)]
    public void Engine_name_match_is_case_insensitive(string? name, bool expected)
        => Assert.Equal(expected, AverVersion.IsEngineNameMatch(name));
}
