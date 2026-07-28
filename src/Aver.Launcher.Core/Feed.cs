using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aver.Launcher.Core;

/// <summary>One file in a published version.</summary>
public sealed class FileEntry
{
    /// <summary>Path relative to the install root, with backslashes.</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;

    [JsonPropertyName("size")] public long Size { get; set; }

    /// <summary>Lower-case hex SHA-256. This is the deduplication key across every version.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
}

/// <summary>Where a pack lives and how to verify it.</summary>
public sealed class PackRef
{
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
}

/// <summary>What a build needs from the machine.</summary>
public sealed class Requirements
{
    [JsonPropertyName("minWindowsBuild")] public int MinWindowsBuild { get; set; } = PrereqReport.MinWindowsBuild;
    [JsonPropertyName("d3d12")] public bool D3D12 { get; set; } = true;
    [JsonPropertyName("diskBytes")] public long DiskBytes { get; set; }
}

/// <summary>The per-version manifest: the file list, and where to get the bytes.</summary>
public sealed class VersionManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("engineName")] public string EngineName { get; set; } = AverVersion.EngineName;
    [JsonPropertyName("edition")] public string Edition { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("publishedUtc")] public string PublishedUtc { get; set; } = string.Empty;
    [JsonPropertyName("entryPoint")] public string EntryPoint { get; set; } = @"bin\Sandbox.exe";
    [JsonPropertyName("options")] public Dictionary<string, bool> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("requires")] public Requirements Requires { get; set; } = new();
    [JsonPropertyName("files")] public List<FileEntry> Files { get; set; } = [];
    [JsonPropertyName("pack")] public PackRef Pack { get; set; } = new();

    /// <summary>Total installed size.</summary>
    public long TotalBytes => Files.Sum(f => f.Size);

    public EngineEdition ToEdition()
    {
        var e = new EngineEdition { Id = Edition, DisplayName = $"Aver Engine ({Edition})" };
        foreach (KeyValuePair<string, bool> kv in Options) e.Options[kv.Key] = kv.Value;
        return e;
    }
}

/// <summary>An edition as advertised by the feed.</summary>
public sealed class FeedEdition
{
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("latest")] public string Latest { get; set; } = string.Empty;
    [JsonPropertyName("versions")] public List<string> Versions { get; set; } = [];
    [JsonPropertyName("options")] public Dictionary<string, bool> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The small document every launcher polls.</summary>
public sealed class FeedIndex
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("generatedUtc")] public string GeneratedUtc { get; set; } = string.Empty;
    [JsonPropertyName("minLauncherVersion")] public string MinLauncherVersion { get; set; } = "1.0.0";
    [JsonPropertyName("editions")] public Dictionary<string, FeedEdition> Editions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Template for a version manifest URL, with <c>{edition}</c> and <c>{version}</c> placeholders.
    /// </summary>
    /// <remarks>
    /// A template rather than a per-version URL list, so the index stays small as versions accumulate
    /// and so the host can be changed by regenerating one document.
    /// </remarks>
    [JsonPropertyName("manifestUrl")] public string ManifestUrl { get; set; } = string.Empty;

    /// <summary>The launcher's own supported schema. Anything higher is refused, not guessed at.</summary>
    public const int SupportedSchema = 1;

    public string ManifestUrlFor(string edition, string version)
        => ManifestUrl.Replace("{edition}", edition, StringComparison.Ordinal)
                      .Replace("{version}", version, StringComparison.Ordinal);
}

/// <summary>JSON settings shared by everything that reads or writes feed documents.</summary>
public static class FeedJson
{
    /// <summary>
    /// Unknown members are SKIPPED, which is the one forward-compatibility rule the whole system
    /// uses -- the same guarantee <c>.ocproject</c> makes. A feed written by a later publisher must
    /// load in an older launcher.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
