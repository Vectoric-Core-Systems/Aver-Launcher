using Aver.Launcher.Core;
using Xunit;

namespace Aver.Launcher.Tests;

/// <summary>
/// The polling rules, against a fixed clock. These exist because the alternative way to test "does
/// it poll every four hours" is to wait four hours.
/// </summary>
public class UpdateCheckPolicyTests
{
    private static readonly DateTime Now = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Launch_always_checks_when_not_backed_off()
    {
        var fresh = new FeedState();
        Assert.True(UpdateCheckPolicy.ShouldCheck(CheckTrigger.Launch, fresh, Now));

        // Even seconds after the last one: the window is about to show a list, and showing a stale
        // one when the answer was one request away is the thing this exists to avoid.
        var justChecked = new FeedState { LastCheckUtc = Now.AddSeconds(-5) };
        Assert.True(UpdateCheckPolicy.ShouldCheck(CheckTrigger.Launch, justChecked, Now));
    }

    [Theory]
    [InlineData(0, true)]      // never checked
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]      // the interval exactly
    [InlineData(9, true)]
    public void Timer_waits_for_the_interval(int hoursAgo, bool expected)
    {
        var state = new FeedState
        {
            LastCheckUtc = hoursAgo == 0 ? null : Now.AddHours(-hoursAgo),
        };
        Assert.Equal(expected, UpdateCheckPolicy.ShouldCheck(CheckTrigger.Timer, state, Now));
    }

    [Theory]
    [InlineData(5, false)]     // alt-tabbing must not poll
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(120, true)]
    public void Activation_is_rate_limited(int minutesAgo, bool expected)
    {
        var state = new FeedState { LastCheckUtc = Now.AddMinutes(-minutesAgo) };
        Assert.Equal(expected, UpdateCheckPolicy.ShouldCheck(CheckTrigger.Activated, state, Now));
    }

    [Fact]
    public void Backoff_suppresses_every_automatic_trigger()
    {
        var state = new FeedState
        {
            LastCheckUtc = Now.AddDays(-1),          // long overdue
            BackoffUntilUtc = Now.AddMinutes(10),
        };

        Assert.False(UpdateCheckPolicy.ShouldCheck(CheckTrigger.Launch, state, Now));
        Assert.False(UpdateCheckPolicy.ShouldCheck(CheckTrigger.Timer, state, Now));
        Assert.False(UpdateCheckPolicy.ShouldCheck(CheckTrigger.Activated, state, Now));
    }

    [Fact]
    public void Manual_overrides_the_backoff()
    {
        // Deliberate deviation from the original plan, which had Manual bypass only the rate limit.
        // Someone who just reconnected their network and pressed Refresh has better information than
        // a timer that last failed offline, and a backoff that ignores an explicit request is one the
        // user works around by restarting the app.
        var state = new FeedState { BackoffUntilUtc = Now.AddHours(1) };
        Assert.True(UpdateCheckPolicy.ShouldCheck(CheckTrigger.Manual, state, Now));
    }

    [Fact]
    public void Backoff_climbs_the_ladder_and_stays_at_the_top()
    {
        var state = new FeedState();
        int[] expectedMinutes = [1, 5, 15, 60, 60, 60];

        foreach (int minutes in expectedMinutes)
        {
            UpdateCheckPolicy.RecordFailure(state, Now);
            Assert.Equal(Now.AddMinutes(minutes), state.BackoffUntilUtc);
        }

        Assert.Equal(expectedMinutes.Length, state.ConsecutiveFailures);
    }

    [Fact]
    public void Success_clears_the_backoff_and_stores_the_etag()
    {
        var state = new FeedState();
        UpdateCheckPolicy.RecordFailure(state, Now);
        UpdateCheckPolicy.RecordFailure(state, Now);
        Assert.NotNull(state.BackoffUntilUtc);

        UpdateCheckPolicy.RecordSuccess(state, Now, "\"abc123\"");

        Assert.Null(state.BackoffUntilUtc);
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.Equal("\"abc123\"", state.Etag);
        Assert.Equal(Now, state.LastCheckUtc);
    }

    [Fact]
    public void A_success_with_no_etag_keeps_the_previous_one()
    {
        // A 304 carries no ETag header of its own; discarding the stored one would turn every
        // subsequent poll into a full download of the index.
        var state = new FeedState { Etag = "\"kept\"" };
        UpdateCheckPolicy.RecordSuccess(state, Now, null);
        Assert.Equal("\"kept\"", state.Etag);
    }

    [Fact]
    public void Timer_delay_is_jittered_within_bounds()
    {
        var rng = new Random(1234);
        for (int i = 0; i < 200; i++)
        {
            TimeSpan d = UpdateCheckPolicy.NextTimerDelay(rng);
            Assert.InRange(d, UpdateCheckPolicy.TimerInterval,
                           UpdateCheckPolicy.TimerInterval + UpdateCheckPolicy.MaxJitter);
        }
    }

    [Fact]
    public void State_round_trips_through_disk()
    {
        string path = Path.Combine(Path.GetTempPath(), "aver-feedstate-" + Guid.NewGuid().ToString("N") + ".json");
        var state = new FeedState
        {
            Etag = "\"e\"",
            LastCheckUtc = Now,
            ConsecutiveFailures = 3,
            BackoffUntilUtc = Now.AddMinutes(15),
        };
        state.Save(path);

        FeedState back = FeedState.Load(path);
        Assert.Equal(state.Etag, back.Etag);
        Assert.Equal(state.ConsecutiveFailures, back.ConsecutiveFailures);
        Assert.Equal(state.BackoffUntilUtc, back.BackoffUntilUtc);
    }

    [Fact]
    public void A_corrupt_state_file_does_not_stop_the_launcher()
    {
        string path = Path.Combine(Path.GetTempPath(), "aver-feedstate-bad-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ this is not json");
        FeedState back = FeedState.Load(path);
        Assert.Null(back.Etag);
        Assert.Equal(0, back.ConsecutiveFailures);
    }
}

public class UpdateFinderTests
{
    private static EngineInstall Install(string edition, string version)
    {
        var ed = new EngineEdition { Id = edition };
        return new EngineInstall
        {
            Root = $@"C:\r\{edition}\{version}",
            EntryPoint = $@"C:\r\{edition}\{version}\bin\Sandbox.exe",
            Version = version,
            Edition = ed,
        };
    }

    private static FeedIndex Index(params (string Edition, string[] Versions)[] editions)
    {
        var idx = new FeedIndex();
        foreach ((string e, string[] vs) in editions)
        {
            idx.Editions[e] = new FeedEdition { Latest = vs[^1], Versions = [.. vs] };
        }
        return idx;
    }

    [Fact]
    public void A_newer_version_is_reported()
    {
        IReadOnlyList<UpdateAvailable> u = UpdateFinder.Find(
            [Install("standard", "0.1.0")], Index(("standard", ["0.1.0", "0.2.0"])));

        UpdateAvailable one = Assert.Single(u);
        Assert.Equal("standard", one.Edition);
        Assert.Equal("0.1.0", one.InstalledVersion);
        Assert.Equal("0.2.0", one.AvailableVersion);
    }

    [Fact]
    public void Up_to_date_reports_nothing()
        => Assert.Empty(UpdateFinder.Find(
            [Install("standard", "0.2.0")], Index(("standard", ["0.1.0", "0.2.0"]))));

    [Fact]
    public void Several_installed_versions_yield_one_update_for_the_edition()
    {
        // With 0.1.0 and 0.2.0 both here and 0.3.0 offered, there is one update to consider, not two.
        IReadOnlyList<UpdateAvailable> u = UpdateFinder.Find(
            [Install("standard", "0.1.0"), Install("standard", "0.2.0")],
            Index(("standard", ["0.1.0", "0.2.0", "0.3.0"])));

        UpdateAvailable one = Assert.Single(u);
        Assert.Equal("0.2.0", one.InstalledVersion);   // compared against the NEWEST installed
        Assert.Equal("0.3.0", one.AvailableVersion);
    }

    [Fact]
    public void An_edition_the_feed_does_not_carry_is_ignored()
        => Assert.Empty(UpdateFinder.Find(
            [Install("bespoke", "0.1.0")], Index(("standard", ["0.9.0"]))));

    [Fact]
    public void Editions_are_tracked_independently()
    {
        IReadOnlyList<UpdateAvailable> u = UpdateFinder.Find(
            [Install("standard", "0.1.0"), Install("minimal", "0.2.0")],
            Index(("standard", ["0.2.0"]), ("minimal", ["0.2.0"])));

        UpdateAvailable one = Assert.Single(u);
        Assert.Equal("standard", one.Edition);
    }

    [Fact]
    public void Ordering_uses_the_engine_comparison_not_string_order()
    {
        // "0.10.0" sorts before "0.9.0" as a string; the engine compares components numerically.
        IReadOnlyList<UpdateAvailable> u = UpdateFinder.Find(
            [Install("standard", "0.9.0")], Index(("standard", ["0.9.0", "0.10.0"])));
        Assert.Equal("0.10.0", Assert.Single(u).AvailableVersion);
    }
}
