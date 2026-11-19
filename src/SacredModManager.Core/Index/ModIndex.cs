using System.Text.Json;
using System.Text.Json.Serialization;

namespace SacredModManager.Core.Index;

/// <summary>
/// A <c>mod-index.json</c> (schema 1, see docs/ModIndex.md). An index lists mods, points to the zip that holds them and may also
/// reference other indexes; all three parts are optional and independent. Unknown fields are ignored.
/// </summary>
public sealed class ModIndex
{
    public int Schema { get; set; } = 1;
    public string? Name { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Homepage { get; set; }
    public IndexDownload? Download { get; set; }
    public List<IndexMod>? Mods { get; set; }
    public List<IndexRef>? Indexes { get; set; }
}

/// <summary>Where the zip with the mods of an index comes from.</summary>
public sealed class IndexDownload
{
    public string? Url { get; set; }
    public string? Version { get; set; }

    /// <summary>Optional lower-case hex SHA-256 of the zip; checked after the download when present.</summary>
    public string? Sha256 { get; set; }
}

/// <summary>One mod of an index.</summary>
public sealed class IndexMod
{
    public string? Id { get; set; }

    /// <summary>Folder of the mod inside the zip (forward slashes, e.g. <c>mods/bounty</c>).</summary>
    public string? Path { get; set; }

    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public List<string>? Tags { get; set; }

    /// <summary>A path inside the zip, or an absolute http(s) URL.</summary>
    public string? Thumbnail { get; set; }

    /// <summary>Pictures of the mod for the viewer: each a path inside the zip or an absolute https URL. The thumbnail is the first one when it has none of its own.</summary>
    public List<string>? Images { get; set; }

    public List<string>? Requires { get; set; }
    public bool? DefaultEnabled { get; set; }

    /// <summary>Overrides the index's download for this mod.</summary>
    public IndexDownload? Download { get; set; }
}

/// <summary>A reference to another index (absolute URL, or relative to the URL of the index that lists it).</summary>
public sealed class IndexRef
{
    public string? Url { get; set; }
    public string? Name { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ModIndex))]
internal sealed partial class IndexJsonContext : JsonSerializerContext;

/// <summary>Parses and writes index documents.</summary>
public static class IndexJson
{
    public static ModIndex Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            ModIndex? index = JsonSerializer.Deserialize(utf8, IndexJsonContext.Default.ModIndex);
            return index ?? throw new IndexException("the file is empty");
        }
        catch (JsonException ex)
        {
            throw new IndexException("not a valid mod index: " + ex.Message);
        }
    }

    public static string ToJson(ModIndex index) => JsonSerializer.Serialize(index, IndexJsonContext.Default.ModIndex);
}

/// <summary>An index that cannot be used.</summary>
public sealed class IndexException(string message) : Exception(message);
