using System.Text.Json.Serialization;

namespace Aver.Launcher.Core;

/// <summary>Why a feed check is being attempted.</summary>
public enum CheckTrigger
{
    /// <summary>The launcher just started.</summary>
    Launch,

    /// <summary>The background interval elapsed.</summary>
    Timer,

    /// <summary>The window came to the foreground.</summary>
    Activated,

    /// <summary>The user pressed Refresh.</summary>
    Manual,
}

/// <summary>
/// Machine-written state about feed polling. Separate from <see cref="LauncherSettings"/>.
/// </summary>
/// <remarks>
/// Kept in its own file because settings are hand-editable and this is not: a user opening
/// settings.json should not meet an ETag and a backoff deadline, and the launcher rewriting the file
/// they are editing is how hand-edits get lost.
/// </remarks>
public sealed class FeedState
{
    /// <summary>Last ETag the feed returned, so an unchanged poll costs a 304 with no body.</summary>
    [JsonPropertyName("etag")] public string? Etag { get; set; }

    [JsonPropertyName("lastCheckUtc")] public DateTime? LastCheckUtc { get; set; }

    /// <summary>Successive failures. Drives the backoff ladder; reset on any success.</summary>
    [JsonPropertyName("consecutiveFailures")] public int ConsecutiveFailures { get; set; }

    /// <summary>Do not poll before this. Persisted, so restarting does not reset the ladder.</summary>
    [JsonPropertyName("backoffUntilUtc")] public DateTime? BackoffUntilUtc { get; set; }

    public static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Aver", "Launcher", "feed-state.json");

    public static FeedState Load(string? path = null)
    {
        path ??= Path_;
        try
        {
            if (File.Exists(path)) return FeedJson.Read<FeedState>(File.ReadAllText(path)) ?? new FeedState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Corrupt state must not stop the launcher; the worst case is one extra poll.
        }
        return new FeedState();
    }

    public void Save(string? path = null)
    {
        path ??= Path_;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, FeedJson.Write(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// When to poll the feed.
/// </summary>
/// <remarks>
/// <para>
/// There is no push channel and there will not be one: the feed is static files on a CDN. So the
/// launcher polls, and the whole design problem is doing that often enough to be useful without
/// hammering a host shared by every launcher behind the same NAT.
/// </para>
/// <para>
/// A pure function of (trigger, state, now) so every rule can be tested against a fixed clock rather
/// than by waiting four hours.
/// </para>
/// </remarks>
public static class UpdateCheckPolicy
{
    /// <summary>Background interval.</summary>
    public static readonly TimeSpan TimerInterval = TimeSpan.FromHours(4);

    /// <summary>
    /// Random delay added to each timed check so many launchers started at once do not all poll on
    /// the same second forever after.
    /// </summary>
    public static readonly TimeSpan MaxJitter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Focus is cheap to gain and lose. Without this, alt-tabbing would poll on every switch.
    /// </summary>
    public static readonly TimeSpan ActivatedRateLimit = TimeSpan.FromMinutes(30);

    /// <summary>Backoff ladder after consecutive failures.</summary>
    public static readonly TimeSpan[] BackoffLadder =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(60),
    ];

    /// <summary>Whether a check should proceed.</summary>
    /// <remarks>
    /// <b>Manual overrides the backoff.</b> The plan originally had it bypass only the rate limit,
    /// which is wrong in the case that matters: someone who just reconnected their network and
    /// pressed Refresh has better information than a timer that last failed offline. A backoff that
    /// ignores an explicit request is a backoff the user works around by restarting the app.
    /// </remarks>
    public static bool ShouldCheck(CheckTrigger trigger, FeedState state, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (trigger == CheckTrigger.Manual) return true;

        if (state.BackoffUntilUtc is { } until && nowUtc < until) return false;

        return trigger switch
        {
            // Always on launch: the window is about to show a list, and showing a stale one when the
            // answer was one request away is the thing this exists to avoid.
            CheckTrigger.Launch => true,

            CheckTrigger.Timer => state.LastCheckUtc is not { } last
                                  || nowUtc - last >= TimerInterval,

            CheckTrigger.Activated => state.LastCheckUtc is not { } last2
                                      || nowUtc - last2 >= ActivatedRateLimit,

            _ => false,
        };
    }

    /// <summary>Records a successful check, clearing any backoff.</summary>
    public static void RecordSuccess(FeedState state, DateTime nowUtc, string? etag)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.LastCheckUtc = nowUtc;
        state.ConsecutiveFailures = 0;
        state.BackoffUntilUtc = null;
        if (etag is not null) state.Etag = etag;
    }

    /// <summary>Records a failure and advances the backoff ladder.</summary>
    public static void RecordFailure(FeedState state, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.LastCheckUtc = nowUtc;
        state.ConsecutiveFailures++;
        int rung = Math.Min(state.ConsecutiveFailures, BackoffLadder.Length) - 1;
        state.BackoffUntilUtc = nowUtc + BackoffLadder[rung];
    }

    /// <summary>The timer's next interval, jittered.</summary>
    public static TimeSpan NextTimerDelay(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return TimerInterval + TimeSpan.FromSeconds(random.Next(0, (int)MaxJitter.TotalSeconds));
    }
}

/// <summary>A newer version of something already installed.</summary>
/// <param name="Edition">Edition id.</param>
/// <param name="InstalledVersion">What is here now.</param>
/// <param name="AvailableVersion">What the feed offers.</param>
public sealed record UpdateAvailable(string Edition, string InstalledVersion, string AvailableVersion);

/// <summary>Works out which installed editions have a newer version on offer.</summary>
public static class UpdateFinder
{
    /// <summary>
    /// One entry per edition that has a newer version, comparing the NEWEST installed of that
    /// edition against the newest offered.
    /// </summary>
    /// <remarks>
    /// Per edition rather than per install: with 0.1.0 and 0.2.0 of standard both present and 0.3.0
    /// offered, the user has one update to consider, not two.
    /// </remarks>
    public static IReadOnlyList<UpdateAvailable> Find(
        IEnumerable<EngineInstall> installs, FeedIndex index)
    {
        ArgumentNullException.ThrowIfNull(installs);
        ArgumentNullException.ThrowIfNull(index);

        var newestInstalled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (EngineInstall i in installs)
        {
            if (!newestInstalled.TryGetValue(i.Edition.Id, out string? cur)
                || AverVersion.Compare(i.Version, cur) > 0)
            {
                newestInstalled[i.Edition.Id] = i.Version;
            }
        }

        var result = new List<UpdateAvailable>();
        foreach ((string edition, string installed) in newestInstalled)
        {
            if (!index.Editions.TryGetValue(edition, out FeedEdition? fe)) continue;

            string? newest = fe.Versions
                .OrderByDescending(v => v, Comparer<string>.Create(AverVersion.Compare))
                .FirstOrDefault();

            if (newest is not null && AverVersion.Compare(newest, installed) > 0)
            {
                result.Add(new UpdateAvailable(edition, installed, newest));
            }
        }

        return result;
    }
}
