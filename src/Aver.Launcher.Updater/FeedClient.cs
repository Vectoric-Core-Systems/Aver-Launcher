using System.Net;
using System.Net.Http.Headers;
using Aver.Launcher.Core;

namespace Aver.Launcher.Updater;

/// <summary>Fetches ranges over HTTP.</summary>
/// <remarks>
/// <para>
/// The URL is re-resolved per request rather than cached. A GitHub release asset URL 302s to
/// <c>objects.githubusercontent.com</c> carrying a SHORT-LIVED SIGNED token; reusing yesterday's
/// resolved URL gets a 403 that looks exactly like a network fault.
/// </para>
/// <para>
/// A server that ignores <c>Range</c> and returns 200 with the whole body is handled rather than
/// trusted: the requested slice is taken from what arrived. That is correctness, not optimisation --
/// silently writing whole-pack bytes into a file's slot would produce garbage that only surfaces at
/// the SHA-256 check, with a misleading message.
/// </para>
/// </remarks>
public sealed class HttpRangeFetcher(HttpClient http, string url) : IRangeFetcher
{
    private byte[]? _whole;

    public long BytesTransferred { get; private set; }

    public int RequestCount { get; private set; }

    /// <summary>True when the server ignored <c>Range</c> and the whole object had to be taken.</summary>
    public bool ServerIgnoredRange { get; private set; }

    public async Task<byte[]> FetchAsync(long offset, long length, CancellationToken ct = default)
    {
        // A server that already proved it ignores Range is not asked again. Without this the
        // installer re-downloads the entire pack once per range: measured at 300% of the pack over
        // three ranges, which is worse than not deduplicating at all.
        if (_whole is not null) return Slice(_whole, offset, length);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);

        using HttpResponseMessage res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                                 .ConfigureAwait(false);
        res.EnsureSuccessStatusCode();

        byte[] body = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        RequestCount++;
        BytesTransferred += body.Length;

        if (res.StatusCode == HttpStatusCode.PartialContent) return body;

        // 200: the server sent the whole object. Keep it and serve every later range from memory,
        // so the cost is one full download rather than one per range. Packs are tens of MB, which is
        // affordable; the alternative is not.
        ServerIgnoredRange = true;
        _whole = body;
        return Slice(body, offset, length);
    }

    private static byte[] Slice(byte[] body, long offset, long length)
    {
        if (offset + length > body.Length)
        {
            throw new InvalidDataException(
                $"server ignored Range and returned {body.Length} bytes, too short for {offset}+{length}");
        }
        return body.AsSpan((int)offset, (int)length).ToArray();
    }
}

/// <summary>Reads ranges from a local file. Used by the offline gates and a file:// feed.</summary>
public sealed class FileRangeFetcher(string path) : IRangeFetcher
{
    public long BytesTransferred { get; private set; }

    public Task<byte[]> FetchAsync(long offset, long length, CancellationToken ct = default)
    {
        using FileStream fs = File.OpenRead(path);
        fs.Position = offset;
        byte[] buf = new byte[length];
        fs.ReadExactly(buf);
        BytesTransferred += length;
        return Task.FromResult(buf);
    }
}

/// <summary>Polls the feed and fetches manifests.</summary>
public sealed class FeedClient
{
    private readonly HttpClient _http;
    private string? _etag;

    public FeedClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        });

        // GitHub rejects requests that send no User-Agent.
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("AverLauncher/1.0 (+https://github.com/hydrogen-isotope/Aver-Launcher)");
        }
    }

    public HttpClient Http => _http;

    /// <summary>Last poll returned 304, meaning nothing changed.</summary>
    public bool NotModified { get; private set; }

    /// <summary>
    /// Fetches the index, using <c>If-None-Match</c> so an unchanged poll costs a 304 with no body.
    /// Returns null when unchanged.
    /// </summary>
    public async Task<FeedIndex?> GetIndexAsync(string indexUrl, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, indexUrl);
        if (_etag is not null) req.Headers.IfNoneMatch.ParseAdd(_etag);

        using HttpResponseMessage res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotModified)
        {
            NotModified = true;
            return null;
        }

        res.EnsureSuccessStatusCode();
        NotModified = false;
        _etag = res.Headers.ETag?.ToString();

        string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        FeedIndex index = FeedJson.Read<FeedIndex>(json)
                          ?? throw new InvalidDataException("the feed index did not parse");

        // Refuse a newer schema rather than guessing at it. minLauncherVersion makes the message
        // precise instead of "something went wrong".
        if (index.SchemaVersion > FeedIndex.SupportedSchema)
        {
            throw new NotSupportedException(
                $"this feed uses schema {index.SchemaVersion}; this launcher understands "
                + $"{FeedIndex.SupportedSchema}. Update the launcher (needs {index.MinLauncherVersion} or later).");
        }

        return index;
    }

    public async Task<VersionManifest> GetManifestAsync(string url, CancellationToken ct = default)
    {
        string json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        return FeedJson.Read<VersionManifest>(json)
               ?? throw new InvalidDataException($"the manifest at {url} did not parse");
    }

    /// <summary>
    /// Reads a pack's index with two ranged requests: the 32-byte header, then the index it points at.
    /// </summary>
    public static async Task<PackIndex> GetPackIndexAsync(IRangeFetcher fetch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        byte[] header = await fetch.FetchAsync(0, AverPack.HeaderSize, ct).ConfigureAwait(false);
        (long off, long len) = AverPack.ReadHeader(header);
        byte[] index = await fetch.FetchAsync(off, len, ct).ConfigureAwait(false);
        return AverPack.ReadIndex(index);
    }
}
