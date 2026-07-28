# The Aver update feed

The wire format between `averdist` (publisher) and the launcher (consumer). Two documents and one
binary container, all served as static files. There is no server-side compute anywhere in this
design, and nothing here requires the GitHub API.

## Where it lives

| What | Where | URL |
|---|---|---|
| engine `.averpack` | release asset on `hydrogen-isotope/Aver-Engine` | `.../releases/download/v0.1.0/full-0.1.0.averpack` |
| engine `.zip` | same release | `.../releases/download/v0.1.0/AverEngine-0.1.0-win-x64.zip` |
| `index.json` | asset on **every** release | `.../releases/latest/download/index.json` |
| per-version manifest | asset on that version's release | `.../releases/download/v0.1.0/manifest-standard-0.1.0.json` |

`releases/latest/download/<asset>` always resolves to the newest non-prerelease release's asset, so
the feed has a stable URL with **zero repository configuration** — no GitHub Pages to enable, no
branch to maintain. Two consequences to design around:

- `latest` skips prereleases, so a `preview` channel cannot be served this way. When one is wanted,
  either enable Pages or publish a second index under a fixed non-prerelease "feed" release. The
  launcher therefore treats the feed URL as configuration, not a constant.
- **Never poll the GitHub API.** Unauthenticated it is 60 requests/hour/IP, which every launcher
  polling would exhaust between them. Asset downloads are not API calls and are not counted.

Release assets rather than committed files, because assets never enter git history: the repo stays
at kilobytes instead of accumulating ~21 MB per version forever, and assets honour `Range` requests,
which the partial-download design depends on.

> The `github.com/...` asset URL **302s to `objects.githubusercontent.com` with a short-lived signed
> token.** Re-resolve the redirect on every resume; never persist the CDN URL. A resumed download
> that reuses yesterday's signed URL gets a 403 that looks like a network fault.

## `index.json`

Small, polled, and the only document fetched when nothing has changed.

```json
{
  "schemaVersion": 1,
  "generatedUtc": "2026-07-28T16:20:00Z",
  "minLauncherVersion": "1.0.0",
  "channels": {
    "stable": { "editions": { "standard": { "latest": "0.1.0", "versions": ["0.1.0"] } } }
  },
  "editions": {
    "standard": {
      "displayName": "Aver Engine (Standard)",
      "options": {
        "AVER_MODULE_VOXI": true, "AVER_MODULE_PBR": true, "AVER_MODULE_SCRIPTING": true,
        "AVER_MODULE_SCENE": true, "AVER_MODULE_FRAMEWORK": true, "AVER_MODULE_PHYSICS": true,
        "AVER_RHI_D3D12": true, "AVER_RHI_D3D11": true, "AVER_RHI_VULKAN": false,
        "AVER_ENABLE_UI": true
      }
    }
  },
  "urls": {
    "manifest": ".../releases/download/v{version}/manifest-{edition}-{version}.json"
  },
  "templates": {
    "firstperson": {
      "displayName": "First Person",
      "repo": "hydrogen-isotope/Aver-Engine-Templates",
      "path": "FirstPerson/SkyForge"
    }
  }
}
```

`editions[].options` is read verbatim out of the build tree's `CMakeCache.txt`, which is the only
machine-readable record of which modules were compiled in — there is no generated config header.

> **An option that is ABSENT is not an option that is OFF.** Absent means the cache predates the
> option, so the edition is unknowable. `stage-payload.ps1` refuses rather than guessing, and this is
> not hypothetical: `build-release/CMakeCache.txt` lacked `AVER_MODULE_PHYSICS`, `SCENE` and
> `FRAMEWORK` entirely while looking like a perfectly normal Release tree.

## Per-version manifest

```json
{
  "schemaVersion": 1,
  "engineName": "Aver",
  "edition": "standard",
  "version": "0.1.0",
  "publishedUtc": "2026-07-28T16:20:00Z",
  "entryPoint": "bin\\Sandbox.exe",
  "buildProvenance": { "config": "Release", "sourceCommit": "b1df3b7...", "sourceDirty": false },
  "requires": {
    "minWindowsBuild": 17134,
    "d3d12": true,
    "vcRedistMinVersion": "14.20.0",
    "dotnetRuntime": { "framework": "Microsoft.NETCore.App", "minVersion": "10.0.0", "rollForward": "LatestMinor" },
    "dotnetSdkRequiredFor": ["scripting"],
    "diskBytes": 22369621
  },
  "files": [
    { "path": "bin\\Sandbox.exe", "size": 2158080, "sha256": "..." }
  ],
  "packs": {
    "full": {
      "url": ".../releases/download/v0.1.0/full-0.1.0.averpack",
      "size": 12345678, "sha256": "...",
      "blockSize": 1048576, "blockSha256": ["...", "..."]
    },
    "deltas": []
  }
}
```

Two absences are the whole design, and are worth stating so nobody "fixes" them:

- **`files[]` has no `chunks[]`.** Whole-file identity by SHA-256 is the deduplication mechanism.
  Content-defined chunking was measured against `Aver.Physics.dll` under realistic relink
  perturbations and re-downloaded 73–88% of the file for the edit patterns an MSVC relink actually
  produces, versus 2–18 KB for a per-file binary delta — because CDC pays a 32 KB minimum per 4-byte
  fixup, and 21 of the 40 unique payload files are smaller than that minimum anyway. The launcher
  already has the previous version whole on disk; that is a better reference than any chunk store.
- **No `deletes[]`.** Installs go into a fresh directory, so a file absent from `files[]` is simply
  never created. "Wiping the replaced ones" is uninstalling the old *version*, which is an explicit
  action with a keep-last-N setting — not in-place mutation, which is where every updater bug lives.

`blockSha256` covers 1 MiB blocks of the **pack**, and lives here rather than inside the pack so the
launcher can verify a block before it holds the whole file, and re-fetch only the bad ones. This is
the one place fixed-size blocking is correct: it is transport integrity, not content similarity.

## `.averpack`

```
[16 B] "AVERPACK\0" + u16 formatVersion + u16 flags + u32 reserved
[ 8 B] u64 indexOffset
[ 8 B] u64 indexLength
[ ...] entry blobs, back to back, 4096-aligned
[idx ] Brotli(UTF-8 JSON pack index)
[32 B] sha256 of everything above
```

Index at the **end**, pointed to by the header, so the publisher can stream blobs without a
two-pass rewrite and the launcher can read the index in two ranged GETs. Entries:

```json
{ "path": "bin\\Sandbox.exe", "op": "full", "sha256": "...", "size": 2158080,
  "off": 4096, "len": 812345, "codec": "brotli" }
```

`op` is `full` (the whole file, compressed), `delta` (an `AverDelta v1` stream against `refPath` +
`refSha256`), or `same` (no payload — present so the launcher can assert its inventory matches the
publisher's assumption and **fail loudly** rather than silently reconstruct wrong bytes).

The publisher sorts entries so likely-to-change files are adjacent, which collapses an update into
one or two coalesced range requests instead of dozens.

### The full pack is also the universal delta

Whole-file identity means the launcher already knows which files it needs before it fetches anything.
The pack index gives their byte ranges, so it fetches only those. A user twelve versions behind
downloads the changed files out of the newest full pack — roughly 4 MB of a 21 MB payload, since
`dxcompiler.dll` (14.3 MB), `dxil.dll`, `nethost.dll`, both Roboto fonts and the artwork are
byte-identical across versions.

So: **no published deltas are required, and deltas are never chained.** Resolution order is

1. a `delta-<installed>-<target>` pack, if one was published;
2. otherwise ranged GETs into the full pack for the entries actually needed;
3. otherwise (version unknown to the feed — a hand-built engine) the whole pack.

Multi-range (`bytes=a-b,c-d`) support on `objects.githubusercontent.com` is **not guaranteed and
must be measured before being relied on**; the fallback is to coalesce ranges with a gap tolerance
and issue sequential single-range GETs.

## Forward compatibility

One rule, mirroring the guarantee `.ocproject` already makes, so there is a single policy to remember:

- **Unknown members are ignored** (`JsonSerializerOptions.UnmappedMemberHandling.Skip`).
- `schemaVersion` is a **major** number. Additive fields must not bump it. The launcher refuses
  `schemaVersion` greater than it knows and uses `minLauncherVersion` to say so precisely.
- Version ordering uses `AverVersion.Compare`, the port of the engine's `compareVersions` — not
  SemVer and not `System.Version`, both of which disagree with the parser that actually gates
  project loads.

## Update detection

Polling, with no push infrastructure and no server:

- on launch (3 s timeout; the UI paints from cache first so a slow network never delays a window);
- every 4 hours, plus up to 15 minutes of jitter so many launchers do not synchronise;
- on window activation, rate-limited to once per 30 minutes;
- a manual **Check for updates** that bypasses the rate limit but not the backoff.

`If-None-Match` against the stored `ETag` makes an unchanged check a 304 with no body. Backoff on
5xx or network failure is 1/5/15/60 minutes and is **persisted**, so restarting does not reset it.
An explicit `User-Agent` is required — GitHub rejects requests that send none.

Latency is honest: CDN cache plus poll interval means minutes, not seconds. The UI says so rather
than implying a push channel exists.
