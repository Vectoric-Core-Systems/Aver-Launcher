using System.Text.Json.Serialization;

namespace Aver.Launcher.Core;

/// <summary>The launcher's own persisted settings.</summary>
public sealed class LauncherSettings
{
    /// <summary>
    /// Where the feed lives. An http(s) URL, or a directory holding <c>index.json</c>.
    /// </summary>
    /// <remarks>
    /// The default points at the release asset that will carry the index once a release exists.
    /// <c>releases/latest/download/&lt;asset&gt;</c> always resolves to the newest non-prerelease
    /// release's asset, which gives a stable feed URL with no repository configuration at all -- no
    /// Pages to enable, no branch to maintain. Until something is published it simply 404s, which the
    /// UI reports as "no feed" rather than pretending.
    /// </remarks>
    [JsonPropertyName("feedUrl")]
    public string FeedUrl { get; set; } = DefaultFeedUrl;

    public const string DefaultFeedUrl =
        "https://github.com/hydrogen-isotope/Aver-Engine/releases/latest/download/index.json";

    /// <summary>Where engines are installed. Empty means the per-user default.</summary>
    [JsonPropertyName("installRoot")]
    public string InstallRoot { get; set; } = string.Empty;

    /// <summary>Versions to keep when a newer one is installed. Uninstall is explicit, never silent.</summary>
    [JsonPropertyName("keepVersions")]
    public int KeepVersions { get; set; } = 2;

    [JsonIgnore]
    public string EffectiveInstallRoot =>
        InstallRoot.Length > 0 ? InstallRoot : EngineInstallStore.DefaultRoot;

    public static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Aver", "Launcher", "settings.json");

    public static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                return FeedJson.Read<LauncherSettings>(File.ReadAllText(Path_)) ?? new LauncherSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // A corrupt settings file must not stop the launcher starting.
        }
        return new LauncherSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, FeedJson.Write(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
