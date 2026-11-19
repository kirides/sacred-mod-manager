using System.Text.Json;
using System.Text.Json.Serialization;

namespace SacredModManager.Core.Mods;

/// <summary>The record next to a backup: which folder it was, what the manager knew about it.</summary>
public sealed class BackupMeta
{
    public string Folder { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string When { get; set; } = string.Empty;
    public TrackedMod? Tracked { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(BackupMeta))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;
