using System.Text.Json;
using System.Text.Json.Serialization;

namespace SacredModManager.Core.App;

/// <summary>The manager's own settings (<c>%APPDATA%\SacredModManager\settings.json</c>).</summary>
public sealed class AppSettings
{
    /// <summary>The default source: the mods of the Sacred Gear project.</summary>
    public const string DefaultSource = "https://raw.githubusercontent.com/kirides/sacred-gear-mods/main/mod-index.json";

    /// <summary>The index of Sacred-Bild and Sacred Gear (the plugins). It points to the index of the mods too, so it brings all three.</summary>
    public const string SacredBildSource = "https://github.com/kirides/sacred-bild";

    public string GameFolder { get; set; } = string.Empty;

    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; set; } = "system";

    /// <summary>Addresses of the mod indexes the player added (the default source is listed too, so it can be removed).</summary>
    public List<string> Sources { get; set; } = [.. DefaultSources];

    /// <summary>The sources a fresh install starts with: Sacred-Bild (with Sacred Gear), which brings the mods along, and the mods themselves.</summary>
    public static IReadOnlyList<string> DefaultSources { get; } = [SacredBildSource, DefaultSource];

    /// <summary>Set once the Sacred-Bild source was added to a settings file that predates it (so a source the player removes later stays removed).</summary>
    public bool? SacredBildSourceOffered { get; set; }

    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool AskBeforeRemove { get; set; } = true;

    /// <summary>Ask GitHub now and then whether a newer version of the manager exists (it only tells; nothing is downloaded).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>The tag of the version whose banner the player closed (it is not shown again for that version).</summary>
    public string DismissedUpdate { get; set; } = string.Empty;

    /// <summary>Where tools are unpacked; empty: the manager's own tools folder.</summary>
    public string ToolsFolder { get; set; } = string.Empty;

    /// <summary>Register the manager as the handler of gear-mod-manager:// links (Windows and Linux, current user only).</summary>
    public bool RegisterLinks { get; set; } = true;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

public static class SettingsFile
{
    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                AppSettings? s = JsonSerializer.Deserialize(File.ReadAllBytes(path), SettingsJsonContext.Default.AppSettings);
                if (s is not null)
                {
                    s.Sources = (s.Sources ?? [.. AppSettings.DefaultSources]).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (s.SacredBildSourceOffered != true)
                    {
                        // a settings file from before Sacred-Bild was a default: offer it once
                        if (!s.Sources.Contains(AppSettings.SacredBildSource, StringComparer.OrdinalIgnoreCase))
                        {
                            s.Sources.Insert(0, AppSettings.SacredBildSource);
                        }

                        s.SacredBildSourceOffered = true;
                    }

                    return s;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // unreadable settings: start with the defaults
        }

        return new AppSettings { SacredBildSourceOffered = true };
    }

    public static void Save(string path, AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Default.AppSettings));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // settings are a convenience: failing to save them must not stop the app
        }
    }
}
