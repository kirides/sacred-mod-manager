using System.Text.Json;
using System.Text.Json.Serialization;

namespace SacredModManager.Core.Mods;

/// <summary>What the manager knows about a mod it installed: where it came from and the files it wrote (with their hashes).</summary>
public sealed class TrackedMod
{
    public string Id { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;

    /// <summary>"mod" (omitted in old files), "plugin" or "tool".</summary>
    public string? Kind { get; set; }

    /// <summary>Tools: the program to start, relative to the tool's folder.</summary>
    public string? Launch { get; set; }

    /// <summary>Plugins: files that existed in the game folder before and were backed up when the plugin replaced them (put back when it is removed).</summary>
    public List<string>? Replaced { get; set; }

    public string Version { get; set; } = string.Empty;
    public string? IndexUrl { get; set; }
    public string? PackageUrl { get; set; }
    public string? InstalledAt { get; set; }

    public ItemKind ItemKind => ItemKinds.Parse(Kind) ?? ItemKind.Mod;

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
                return JsonSerializer.Deserialize(StripBom(File.ReadAllBytes(Path)), RegistryJsonContext.Default.InstallRegistryFile) ?? new InstallRegistryFile();
            }
        }
        catch (JsonException)
        {
            // an unreadable record only means "nothing is tracked": the mods themselves stay as they are
            File.Copy(Path, Path + ".bak", overwrite: true);
        }

        return new InstallRegistryFile();
    }

    private static ReadOnlySpan<byte> StripBom(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes.AsSpan(3) : bytes;

    public void Save(InstallRegistryFile file)
    {
        string tmp = Path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(file, RegistryJsonContext.Default.InstallRegistryFile));
        File.Move(tmp, Path, overwrite: true);
    }
}
