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

    /// <summary>Fields this version does not know. Never acted on; only looked at to warn about ones that sound like "run something" (see <see cref="CodeFields"/>).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Where the zip with the mods of an index comes from.</summary>
public sealed class IndexDownload
{
    public string? Url { get; set; }
    public string? Version { get; set; }

    /// <summary>Optional lower-case hex SHA-256 of the zip; checked after the download when present.</summary>
    public string? Sha256 { get; set; }

    /// <summary>
    /// Instead of <see cref="Url"/>: <c>owner/repo</c> of a GitHub repository. The manager asks GitHub for its newest release that has an asset
    /// matching <see cref="Asset"/> (a name with * and ?), so the asset may carry a version or commit in its name. The tag becomes the version
    /// of the download (and the version of an entry that gives none), the asset's digest the checksum.
    /// </summary>
    public string? Github { get; set; }

    /// <summary>With <see cref="Github"/>: the asset name pattern, for example <c>SacredGear-git-*.zip</c>.</summary>
    public string? Asset { get; set; }

    /// <summary>With <see cref="Github"/>: also consider pre-releases (for nightly builds). Default false.</summary>
    public bool? Prerelease { get; set; }

    /// <summary>Fields this version does not know. Never acted on; only looked at to warn about ones that sound like "run something" (see <see cref="CodeFields"/>).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Which files of the package an item puts where (plugins), or where its program is (tools).</summary>
public sealed class InstallSpec
{
    /// <summary>Plugins: a folder of the game folder the files go to (default: the game folder itself).</summary>
    public string? To { get; set; }

    /// <summary>Plugins: the files to copy; each is a path inside the item's folder in the zip, or <c>{"from": ..., "to": ...}</c>. Empty: every file.</summary>
    public List<InstallFileSpec>? Files { get; set; }

    /// <summary>Tools: the program to start, relative to the tool's folder (for example <c>ClassEditor.exe</c>).</summary>
    public string? Launch { get; set; }

    /// <summary>Fields this version does not know. Never acted on; only looked at to warn about ones that sound like "run something" (see <see cref="CodeFields"/>).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One file of a plugin: <c>"ddraw.dll"</c> or <c>{"from": "bin/ddraw.dll", "to": "ddraw.dll"}</c>.</summary>
[JsonConverter(typeof(InstallFileSpecConverter))]
public sealed class InstallFileSpec
{
    public string? From { get; set; }
    public string? To { get; set; }

    /// <summary>A loose file: absolute https address of the file itself (no archive). Needs <see cref="Sha256"/> and <see cref="To"/>.</summary>
    public string? Url { get; set; }

    /// <summary>A loose file: the lower-case hex SHA-256 the downloaded bytes must have.</summary>
    public string? Sha256 { get; set; }
}

/// <summary>Reads a file spec written as a string or as an object.</summary>
public sealed class InstallFileSpecConverter : JsonConverter<InstallFileSpec>
{
    public override InstallFileSpec? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? text = reader.GetString();
            return new InstallFileSpec { From = text, To = text };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        var spec = new InstallFileSpec();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            string name = reader.GetString() ?? string.Empty;
            reader.Read();
            string? value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }

            if (name.Equals("from", StringComparison.OrdinalIgnoreCase))
            {
                spec.From = value;
            }
            else if (name.Equals("to", StringComparison.OrdinalIgnoreCase))
            {
                spec.To = value;
            }
            else if (name.Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                spec.Url = value;
            }
            else if (name.Equals("sha256", StringComparison.OrdinalIgnoreCase))
            {
                spec.Sha256 = value;
            }
        }

        if (spec.Url is null)
        {
            spec.To ??= spec.From;
        }

        return spec;
    }

    public override void Write(Utf8JsonWriter writer, InstallFileSpec value, JsonSerializerOptions options)
    {
        if (value.Url is not null)
        {
            writer.WriteStartObject();
            writer.WriteString("url", value.Url);
            writer.WriteString("sha256", value.Sha256);
            writer.WriteString("to", value.To);
            writer.WriteEndObject();
            return;
        }

        if (value.To is null || value.To == value.From)
        {
            writer.WriteStringValue(value.From);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("from", value.From);
        writer.WriteString("to", value.To);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Indexes are purely declarative: they say what to download and which files to copy where, nothing else. No field of an index (or of anything inside a package)
/// is ever run, evaluated or handed to a shell or an installer. Fields that look like a request to run something are ignored, and the loader says so.
/// </summary>
public static class CodeFields
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "run", "exec", "execute", "script", "scripts", "command", "commands", "cmd", "shell", "installer", "setup", "hook", "hooks",
        "preinstall", "postinstall", "pre_install", "post_install", "pre-install", "post-install", "oninstall", "on_install", "on-install", "autorun", "run_after", "runafter",
    };

    /// <summary>The names in <paramref name="extra"/> that ask for something to be run.</summary>
    public static IEnumerable<string> Find(IDictionary<string, JsonElement>? extra)
        => extra is null ? [] : extra.Keys.Where(Names.Contains);
}

/// <summary>One mod of an index.</summary>
public sealed class IndexMod
{
    public string? Id { get; set; }

    /// <summary>"mod" (default), "plugin" or "tool", see <see cref="Mods.ItemKind"/>.</summary>
    public string? Kind { get; set; }

    /// <summary>Folder of the item inside the zip (forward slashes, e.g. <c>mods/bounty</c>). For plugins and tools it may be empty: the zip's root.</summary>
    public string? Path { get; set; }

    /// <summary>Runtime identifiers the item is for (<c>win-x64</c>, <c>linux-x64</c>, or just <c>win</c> / <c>linux</c>); empty: every platform.</summary>
    public List<string>? Platforms { get; set; }

    /// <summary>Plugins and tools: what to copy where, and what to start.</summary>
    public InstallSpec? Install { get; set; }

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

    /// <summary>Fields this version does not know. Never acted on; only looked at to warn about ones that sound like "run something" (see <see cref="CodeFields"/>).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
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
[JsonSerializable(typeof(InstallFileSpec))]
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
