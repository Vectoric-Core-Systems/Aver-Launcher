using Aver.Launcher.Core;
using Aver.Launcher.Updater;

namespace Aver.Dist.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return (args.Length == 0 ? "help" : args[0]) switch
            {
                "pack" => Pack(args),
                "index" => Index(args),
                "verify" => Verify(args),
                _ => Help(),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"averdist: {ex.Message}");
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            averdist <verb> [options]

              pack   --payload <dir> --out <dir> [--edition <id>] [--base-url <url>]
                     Turns a staged payload into <out>/packs/<edition>-<version>.averpack and
                     <out>/manifests/<edition>/<version>.json.

              index  --out <dir> [--base-url <url>]
                     Regenerates <out>/index.json from every manifest present.

              verify --out <dir>
                     Reconstructs every published pack into a temp tree and hash-checks it against
                     its own manifest. Proves the feed is installable without a launcher.

            Exit codes: 0 ok, 1 failure, 2 usage.
            """);
        return 2;
    }

    private static string? Opt(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string Need(string[] args, string name)
        => Opt(args, name) ?? throw new ArgumentException($"missing {name}");

    // ---------------------------------------------------------------- pack

    private static int Pack(string[] args)
    {
        string payload = Need(args, "--payload");
        string outDir = Need(args, "--out");
        string baseUrl = Opt(args, "--base-url") ?? "";

        string payloadJson = Path.Combine(payload, "payload.json");
        if (!PayloadInfo.TryLoad(payloadJson, out PayloadInfo? info, out string? err))
        {
            Console.Error.WriteLine($"averdist: {err}");
            return 1;
        }

        // The edition id defaults to the payload directory's name, which is what stage-payload.ps1
        // and the install layout already use.
        string edition = Opt(args, "--edition") ?? new DirectoryInfo(payload).Name;

        // Refuse a payload whose module set is unknowable. Absent is not OFF: it means the build
        // predates the option, and publishing it would advertise an edition that never existed.
        var probe = new EngineEdition { Id = edition };
        foreach (KeyValuePair<string, bool> kv in info!.Options) probe.Options[kv.Key] = kv.Value;
        if (!probe.IsComplete(out IReadOnlyList<string> missing))
        {
            Console.Error.WriteLine(
                $"averdist: this payload does not record {string.Join(", ", missing)}. "
                + "An option that is absent is not an option that is OFF, so what it contains cannot "
                + "be stated and it must not be published.");
            return 1;
        }

        Console.WriteLine($"packing {edition} {info.Version} from {payload}");

        // Hash every file. Ordered so that the volatile files sit together: an update then fetches
        // one contiguous run instead of a scatter, and range coalescing has something to work with.
        var files = new List<FileEntry>();
        foreach (string full in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(payload, full).Replace('/', '\\');
            if (rel is "payload.json" or "install-state.json") continue;
            files.Add(new FileEntry
            {
                Path = rel,
                Size = new FileInfo(full).Length,
                Sha256 = AverPack.HashFile(full),
            });
        }

        files = [.. files.OrderBy(f => Volatility(f.Path)).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)];

        Directory.CreateDirectory(Path.Combine(outDir, "packs"));
        Directory.CreateDirectory(Path.Combine(outDir, "manifests", edition));

        string packName = $"{edition}-{info.Version}.averpack";
        string packPath = Path.Combine(outDir, "packs", packName);

        using (FileStream fs = File.Create(packPath))
        {
            AverPack.Write(fs, payload, files, edition, info.Version);
        }

        var manifest = new VersionManifest
        {
            Edition = edition,
            Version = info.Version,
            PublishedUtc = DateTime.UtcNow.ToString("O"),
            EntryPoint = info.EntryPoint.Length > 0 ? info.EntryPoint : @"bin\Sandbox.exe",
            Options = info.Options,
            Files = files,
            Requires = new Requirements { DiskBytes = files.Sum(f => f.Size) },
            Pack = new PackRef
            {
                Url = baseUrl.Length > 0 ? $"{baseUrl.TrimEnd('/')}/packs/{packName}" : $"packs/{packName}",
                Size = new FileInfo(packPath).Length,
                Sha256 = AverPack.HashFile(packPath),
            },
        };

        string manifestPath = Path.Combine(outDir, "manifests", edition, $"{info.Version}.json");
        File.WriteAllText(manifestPath, FeedJson.Write(manifest));

        Console.WriteLine($"  {files.Count} files, {manifest.TotalBytes / 1048576.0:F2} MB installed");
        Console.WriteLine($"  pack {manifest.Pack.Size / 1048576.0:F2} MB -> {packName}");
        Console.WriteLine($"  manifest -> {Path.GetRelativePath(outDir, manifestPath)}");
        return Index(args);
    }

    /// <summary>
    /// Sort key that puts files likely to change together at the FRONT of the pack.
    /// </summary>
    /// <remarks>
    /// The redistributables and artwork are byte-identical between builds, so they are pure
    /// deduplication weight and belong at the back where an update never reads them. Putting the
    /// engine's own binaries first turns a typical update into one contiguous range.
    /// </remarks>
    private static int Volatility(string path)
    {
        string name = Path.GetFileName(path);
        if (name.Equals("Sandbox.exe", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith("Aver.", StringComparison.OrdinalIgnoreCase)) return 1;
        if (path.StartsWith(@"scripting\", StringComparison.OrdinalIgnoreCase)) return 2;
        if (name is "dxcompiler.dll" or "dxil.dll" or "nethost.dll") return 9;
        if (Path.GetExtension(path) is ".ttf" or ".png") return 9;
        return 5;
    }

    // ---------------------------------------------------------------- index

    private static int Index(string[] args)
    {
        string outDir = Need(args, "--out");
        string baseUrl = Opt(args, "--base-url") ?? "";
        string manifestsRoot = Path.Combine(outDir, "manifests");
        if (!Directory.Exists(manifestsRoot))
        {
            Console.Error.WriteLine("averdist: no manifests to index");
            return 1;
        }

        var index = new FeedIndex
        {
            GeneratedUtc = DateTime.UtcNow.ToString("O"),
            ManifestUrl = baseUrl.Length > 0
                ? $"{baseUrl.TrimEnd('/')}/manifests/{{edition}}/{{version}}.json"
                : "manifests/{edition}/{version}.json",
        };

        foreach (string editionDir in Directory.EnumerateDirectories(manifestsRoot))
        {
            string edition = Path.GetFileName(editionDir);
            var versions = new List<string>();
            Dictionary<string, bool> options = new(StringComparer.OrdinalIgnoreCase);

            foreach (string mf in Directory.EnumerateFiles(editionDir, "*.json"))
            {
                VersionManifest? m = FeedJson.Read<VersionManifest>(File.ReadAllText(mf));
                if (m is null) continue;
                versions.Add(m.Version);
                options = m.Options;
            }
            if (versions.Count == 0) continue;

            versions.Sort(AverVersion.Compare);
            index.Editions[edition] = new FeedEdition
            {
                DisplayName = $"Aver Engine ({edition})",
                Latest = versions[^1],
                Versions = versions,
                Options = options,
            };
        }

        string indexPath = Path.Combine(outDir, "index.json");
        File.WriteAllText(indexPath, FeedJson.Write(index));
        Console.WriteLine($"index: {index.Editions.Count} edition(s) -> {indexPath}");
        foreach ((string id, FeedEdition e) in index.Editions)
        {
            Console.WriteLine($"  {id,-10} latest {e.Latest}  ({e.Versions.Count} version(s))");
        }
        return 0;
    }

    // ---------------------------------------------------------------- verify

    private static int Verify(string[] args)
    {
        string outDir = Need(args, "--out");
        string manifestsRoot = Path.Combine(outDir, "manifests");
        int failures = 0;

        foreach (string mf in Directory.EnumerateFiles(manifestsRoot, "*.json", SearchOption.AllDirectories))
        {
            VersionManifest? m = FeedJson.Read<VersionManifest>(File.ReadAllText(mf));
            if (m is null) { Console.Error.WriteLine($"  {mf}: did not parse"); failures++; continue; }

            string packPath = Path.Combine(outDir, "packs", Path.GetFileName(m.Pack.Url));
            if (!File.Exists(packPath)) { Console.Error.WriteLine($"  {m.Edition} {m.Version}: pack missing"); failures++; continue; }

            string actual = AverPack.HashFile(packPath);
            if (!string.Equals(actual, m.Pack.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"  {m.Edition} {m.Version}: pack hash mismatch");
                failures++;
                continue;
            }

            // Reconstruct it. A manifest that hashes correctly but cannot be rebuilt into the tree it
            // describes is not a published version, it is a file that looks like one.
            string tmp = Path.Combine(Path.GetTempPath(), "averdist-verify-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                PackIndex idx = AverPack.ReadIndexFromFile(packPath);
                var installer = new PackInstaller();
                InstallReport report = installer.InstallAsync(
                    m, idx, tmp, new Dictionary<string, string>(), new FileRangeFetcher(packPath))
                    .GetAwaiter().GetResult();

                Console.WriteLine($"  {m.Edition,-10} {m.Version,-8} OK  "
                                  + $"{report.FilesTotal} files, {report.BytesInstalled / 1048576.0:F2} MB");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                Console.Error.WriteLine($"  {m.Edition} {m.Version}: {ex.Message}");
                failures++;
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (IOException) { }
            }
        }

        Console.WriteLine(failures == 0 ? "feed verified" : $"{failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }
}
