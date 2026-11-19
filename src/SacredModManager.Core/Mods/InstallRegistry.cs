using System.Text.Json;
using System.Text.Json.Serialization;

namespace SacredModManager.Core.Mods;

/// <summary>What the manager knows about a mod it installed: where it came from and the files it wrote (with their hashes).</summary>
public sealed class TrackedMod
{
    public string Id { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? IndexUrl { get; set; }
    public string? PackageUrl { get; set; }
    public string? InstalledAt { get; set; }

    /// <summary>Relative path (forward slashes) to SHA-256 (hex) of every file the manager wrote.</summary>
    public Dictionary<string, string> Files { get; set; } = [];
}

/// <summary><c>mods\sacred-mod-manager.json</c>: the manager's own record, kept in the game's mods folder so it travels with the game folder.</summary>
public sealed class InstallRegistryFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, TrackedMod> Mods { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(InstallRegistryFile))]
internal sealed partial class RegistryJsonContext : JsonSerializerContext;

/// <summary>Loads and saves the registry file.</summary>
public sealed class InstallRegistry(string modsDir)
{
    public const string FileName = "sacred-mod-manager.json";

    public string Path { get; } = System.IO.Path.Combine(modsDir, FileName);

    public InstallRegistryFile Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                return JsonSerializer.Deserialize(File.ReadAllBytes(Path), RegistryJsonContext.Default.InstallRegistryFile) ?? new InstallRegistryFile();
            }
        }
        catch (JsonException)
        {
            // an unreadable record only means "nothing is tracked": the mods themselves stay as they are
            File.Copy(Path, Path + ".bak", overwrite: true);
        }

        return new InstallRegistryFile();
    }

    public void Save(InstallRegistryFile file)
    {
        string tmp = Path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(file, RegistryJsonContext.Default.InstallRegistryFile));
        File.Move(tmp, Path, overwrite: true);
    }
}
