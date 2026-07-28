using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aver.Launcher.Core;

/// <summary>
/// A named, prebuilt engine configuration: the unit the launcher actually installs.
/// </summary>
/// <remarks>
/// An edition exists because modules are compile-time options, so "install physics" is not a thing the
/// launcher can do to an existing install -- it can only fetch a different build. Each edition is one
/// full payload with its own manifest, and the chunk-free whole-file identity scheme means two editions
/// sharing 17 MB of identical redistributables and artwork cost that 17 MB once on disk.
/// </remarks>
public sealed class EngineEdition
{
    /// <summary>Feed id, e.g. <c>standard</c>. Lower-case, stable, used in URLs.</summary>
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Optional one-line pitch for the UI.</summary>
    public string? Summary { get; set; }

    /// <summary>
    /// The CMake options this edition was built with, keyed exactly as the cache records them.
    /// </summary>
    public Dictionary<string, bool> Options { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsOn(string key) => Options.TryGetValue(key, out bool v) && v;

    /// <summary>
    /// True when this edition records every option the launcher expects.
    /// </summary>
    /// <remarks>
    /// A MISSING key is not an OFF key. It means the build predates the option, so what the edition
    /// contains is unknown -- and presenting unknown as absent is how a user ends up told they
    /// downloaded a build without physics when they downloaded one built before physics was optional.
    /// </remarks>
    public bool IsComplete(out IReadOnlyList<string> missing)
    {
        var gaps = EngineOptions.EditionDefining
            .Where(k => !Options.ContainsKey(k))
            .ToList();
        missing = gaps;
        return gaps.Count == 0;
    }

    /// <summary>Optional modules that are ON, as display names, in declaration order.</summary>
    public IReadOnlyList<string> IncludedModules =>
        EngineOptions.All
            .Where(o => o.Kind == EngineOptionKind.Module && IsOn(o.Key))
            .Select(o => o.DisplayName)
            .ToList();

    /// <summary>Optional modules that are OFF, paired with what that costs.</summary>
    public IReadOnlyList<(string Module, string Consequence)> ExcludedModules =>
        EngineOptions.All
            .Where(o => o.Kind == EngineOptionKind.Module && !IsOn(o.Key))
            .Select(o => (o.DisplayName, o.Consequence))
            .ToList();

    /// <summary>Render backends compiled into this edition.</summary>
    public IReadOnlyList<string> Backends =>
        EngineOptions.All
            .Where(o => o.Kind == EngineOptionKind.RhiBackend && IsOn(o.Key))
            .Select(o => o.DisplayName)
            .ToList();

    /// <summary>True when this edition can open a project: it needs an editor UI and a real backend.</summary>
    public bool CanEditProjects => IsOn(EngineOptions.EnableUi) && IsOn(EngineOptions.RhiD3D12);

    /// <summary>A short description for a card in the UI, e.g. "Physics, Voxi, Scripting + 2 more".</summary>
    public string ModuleSummary
    {
        get
        {
            IReadOnlyList<string> inc = IncludedModules;
            if (inc.Count == 0) return "no optional modules";
            return inc.Count <= 3
                ? string.Join(", ", inc)
                : $"{string.Join(", ", inc.Take(3))} + {inc.Count - 3} more";
        }
    }
}

/// <summary>
/// The <c>payload.json</c> that <c>stage-payload.ps1</c> writes beside a staged engine tree.
/// </summary>
/// <remarks>
/// This is the launcher's only machine-readable account of what a build contains, because the engine
/// emits no generated config header -- the CMake cache is the sole record, and the staging script is
/// what lifts it out of the build tree into something publishable.
/// </remarks>
public sealed class PayloadInfo
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("engineName")] public string EngineName { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("config")] public string Config { get; set; } = string.Empty;
    [JsonPropertyName("stagedUtc")] public string StagedUtc { get; set; } = string.Empty;
    [JsonPropertyName("withSamples")] public bool WithSamples { get; set; }
    [JsonPropertyName("entryPoint")] public string EntryPoint { get; set; } = string.Empty;
    [JsonPropertyName("sourceCommit")] public string SourceCommit { get; set; } = string.Empty;
    [JsonPropertyName("sourceDirty")] public bool SourceDirty { get; set; }
    [JsonPropertyName("options")] public Dictionary<string, bool> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("fileCount")] public int FileCount { get; set; }
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; set; }

    /// <summary>
    /// Reads a staged payload's descriptor.
    /// </summary>
    /// <remarks>
    /// Unknown members are skipped, matching the one forward-compatibility rule the whole system uses
    /// (and that <c>.ocproject</c> already guarantees), so a payload from a later staging script loads
    /// here rather than being rejected.
    /// </remarks>
    public static bool TryLoad(string path, out PayloadInfo? info, out string? error)
    {
        info = null;
        try
        {
            string json = File.ReadAllText(path);
            info = JsonSerializer.Deserialize<PayloadInfo>(json, Options_);
            if (info is null)
            {
                error = $"{path} parsed as null";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            error = $"cannot read {path}: {ex.Message}";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Turns a staged payload into an edition record.</summary>
    public EngineEdition ToEdition(string id, string? displayName = null)
    {
        var e = new EngineEdition
        {
            Id = id,
            DisplayName = displayName ?? $"Aver Engine ({id})",
        };
        foreach (KeyValuePair<string, bool> kv in Options) e.Options[kv.Key] = kv.Value;
        e.Summary = e.ModuleSummary;
        return e;
    }

    private static readonly JsonSerializerOptions Options_ = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}
