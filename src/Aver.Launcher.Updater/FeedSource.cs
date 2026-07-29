using System.Net;
using Aver.Launcher.Core;

namespace Aver.Launcher.Updater;

/// <summary>How a feed lookup turned out.</summary>
public enum FeedStatus
{
    /// <summary>An index was read.</summary>
    Ok,

    /// <summary>The host answered, but nothing has been published. Not an error.</summary>
    NoReleases,

    /// <summary>Nothing was reachable, or what came back could not be read.</summary>
    Unreachable,

    /// <summary>Unchanged since the last poll (304).</summary>
    NotModified,
}

/// <summary>
/// The outcome of reading a feed.
/// </summary>
/// <param name="Status">Which of the four cases this is.</param>
/// <param name="Index">The index, when there is one.</param>
/// <param name="Message">What to tell the user. Never a raw exception string for the ordinary cases.</param>
/// <remarks>
/// <see cref="FeedStatus.NoReleases"/> exists so the common state of a new project -- a repository
/// that exists with nothing published to it yet -- reads as "nothing to install" rather than as a
/// failure. A 404 on the release asset is exactly what GitHub returns then, and presenting that as an
/// error would make a perfectly healthy setup look broken.
/// </remarks>
public sealed record FeedResult(FeedStatus Status, FeedIndex? Index, string? Message);

/// <summary>
/// Reads a feed over HTTP or from a directory on disk.
/// </summary>
/// <remarks>
/// The local form is not only for tests. A feed is a directory of static files, so a LAN share or a
/// USB stick is a perfectly good one, and an offline or air-gapped install needs no other machinery.
/// It also means the launcher's own install path can be exercised with no server running, which is
/// what makes the download path demonstrable before anything is published.
/// </remarks>
public sealed class FeedSource
{
    private readonly FeedClient? _http;
    private readonly string? _dir;

    private FeedSource(FeedClient? http, string? dir)
    {
        _http = http;
        _dir = dir;
    }

    public bool IsLocal => _dir is not null;

    public string Location { get; private init; } = string.Empty;

    /// <summary>
    /// The conditional-request tag, round-tripped through persisted state. Null for a local feed,
    /// which has no cheap unchanged-check and does not need one.
    /// </summary>
    public string? ETag
    {
        get => _http?.ETag;
        set { if (_http is not null) _http.ETag = value; }
    }

    /// <summary>
    /// Opens a feed. Accepts an http(s) URL, a <c>file://</c> URI, a directory holding
    /// <c>index.json</c>, or the path to an <c>index.json</c> itself.
    /// </summary>
    public static FeedSource Open(string location, FeedClient? client = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);

        if (Uri.TryCreate(location, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return new FeedSource(client ?? new FeedClient(), null) { Location = location };
        }

        string path = uri is { IsFile: true } ? uri.LocalPath : location;
        if (File.Exists(path)) path = Path.GetDirectoryName(path)!;
        return new FeedSource(null, path) { Location = path };
    }

    /// <summary>
    /// Reads the index, distinguishing "nothing published" from "cannot reach it".
    /// </summary>
    /// <remarks>
    /// Never throws for the ordinary outcomes. A launcher pointed at a repository with no releases is
    /// in a normal state, not a broken one, and the difference matters: one is "check back later",
    /// the other is "your network or the URL is wrong".
    /// <para>
    /// The GitHub Releases API is deliberately NOT consulted to tell those apart. Unauthenticated it
    /// allows 60 requests an hour per IP, shared across every launcher behind a NAT, and a poll that
    /// runs on a timer would exhaust it. A 404 on the release asset already means "nothing published",
    /// which is all that needs to be known.
    /// </para>
    /// </remarks>
    public async Task<FeedResult> GetIndexAsync(CancellationToken ct = default)
    {
        try
        {
            if (_dir is not null)
            {
                string p = Path.Combine(_dir, "index.json");
                if (!File.Exists(p))
                {
                    return new FeedResult(FeedStatus.NoReleases, null,
                        $"No index.json in {_dir}. Nothing has been published there yet.");
                }

                FeedIndex? local = FeedJson.Read<FeedIndex>(await File.ReadAllTextAsync(p, ct).ConfigureAwait(false));
                if (local is null) return new FeedResult(FeedStatus.Unreachable, null, "The feed index did not parse.");
                return CheckSchema(local);
            }

            FeedIndex? index = await _http!.GetIndexAsync(Location, ct).ConfigureAwait(false);
            if (index is null) return new FeedResult(FeedStatus.NotModified, null, null);
            return CheckSchema(index);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
        {
            // What GitHub returns for releases/latest/download/<asset> when the repository has no
            // releases, or none carrying that asset. A normal state for a project that has not
            // shipped yet.
            return new FeedResult(FeedStatus.NoReleases, null,
                "No releases have been published yet.");
        }
        catch (HttpRequestException ex)
        {
            return new FeedResult(FeedStatus.Unreachable, null,
                ex.StatusCode is { } code ? $"The feed returned {(int)code} {code}." : ex.Message);
        }
        catch (TaskCanceledException)
        {
            return new FeedResult(FeedStatus.Unreachable, null, "The feed did not respond in time.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return new FeedResult(FeedStatus.Unreachable, null, ex.Message);
        }

        static FeedResult CheckSchema(FeedIndex index)
        {
            if (index.SchemaVersion > FeedIndex.SupportedSchema)
            {
                return new FeedResult(FeedStatus.Unreachable, null,
                    $"This feed uses schema {index.SchemaVersion}; this launcher understands "
                    + $"{FeedIndex.SupportedSchema}. Update the launcher "
                    + $"(needs {index.MinLauncherVersion} or later).");
            }

            // An index that parses but advertises nothing is the same situation as a missing one.
            if (index.Editions.Count == 0)
            {
                return new FeedResult(FeedStatus.NoReleases, index, "The feed lists no editions yet.");
            }

            return new FeedResult(FeedStatus.Ok, index, null);
        }
    }

    public async Task<VersionManifest> GetManifestAsync(FeedIndex index, string edition, string version,
                                                       CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        string url = index.ManifestUrlFor(edition, version);

        if (_dir is not null)
        {
            string p = Path.Combine(_dir, LocalRelative(url));
            return FeedJson.Read<VersionManifest>(await File.ReadAllTextAsync(p, ct).ConfigureAwait(false))
                   ?? throw new InvalidDataException($"the manifest at {p} did not parse");
        }

        return await _http!.GetManifestAsync(url, ct).ConfigureAwait(false);
    }

    /// <summary>A fetcher for a version's pack, and the transfer counter behind it.</summary>
    public (IRangeFetcher Fetcher, Func<long> BytesTransferred) OpenPack(VersionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (_dir is not null)
        {
            string p = Path.Combine(_dir, LocalRelative(manifest.Pack.Url));
            var f = new FileRangeFetcher(p);
            return (f, () => f.BytesTransferred);
        }

        var h = new HttpRangeFetcher(_http!.Http, manifest.Pack.Url);
        return (h, () => h.BytesTransferred);
    }

    /// <summary>
    /// Turns a feed URL into a path relative to the feed directory.
    /// </summary>
    /// <remarks>
    /// A manifest published with <c>--base-url</c> carries ABSOLUTE urls, and combining a directory
    /// with "http://host/packs/x.averpack" produces a path Windows rejects outright. Taking the URL's
    /// path component instead means a feed directory works wherever it is -- copied to a USB stick, a
    /// LAN share, or served over HTTP -- regardless of the base URL it was published with, which is
    /// the property that makes an offline install possible at all.
    /// </remarks>
    private static string LocalRelative(string urlOrPath)
    {
        if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out Uri? u) && !u.IsFile)
        {
            return u.AbsolutePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        }
        return urlOrPath.Replace('/', Path.DirectorySeparatorChar);
    }
}
