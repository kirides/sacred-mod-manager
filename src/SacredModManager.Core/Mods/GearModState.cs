using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SacredModManager.Core.Mods;

/// <summary>
/// <c>mods\gear_mods.json</c>, the file Sacred Gear keeps the player's choices in: <c>{"mods": {"id": {"enabled": bool, "settings": {...},
/// "keys": {...}, "migrated": {...}}}}</c>. The manager only ever touches <c>enabled</c>; everything else (and every field it does not
/// know) is kept as it is. Each change re-reads the file, edits it and writes it back atomically, so a change the game made in the
/// meantime is not lost. A file that cannot be parsed is never overwritten.
/// </summary>
public sealed class GearModState(string modsDir)
{
    public const string FileName = "gear_mods.json";

    public string Path { get; } = System.IO.Path.Combine(modsDir, FileName);

    /// <summary>The saved On/Off choice of each mod (only mods that have one).</summary>
    public Dictionary<string, bool> ReadEnabled()
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        JsonObject root = Load();
        if (root["mods"] is JsonObject mods)
        {
            foreach ((string id, JsonNode? node) in mods)
            {
                if (node is JsonObject e && e["enabled"] is JsonValue v && v.TryGetValue(out bool b))
                {
                    result[id] = b;
                }
            }
        }

        return result;
    }

    /// <summary>Saves the choice; <c>null</c> removes it (the mod's own default applies again).</summary>
    public void SetEnabled(string id, bool? enabled)
    {
        JsonObject root = Load();
        JsonObject mods = root["mods"] as JsonObject ?? [];
        root["mods"] = mods;
        JsonObject entry = mods[id] as JsonObject ?? [];
        if (enabled is { } on)
        {
            entry["enabled"] = on;
            mods[id] = entry;
        }
        else
        {
            entry.Remove("enabled");
            if (entry.Count == 0)
            {
                mods.Remove(id);
            }
        }

        Save(root);
    }

    /// <summary>Removes everything the file keeps about a mod (its choice, settings and key binds).</summary>
    public void Forget(string id)
    {
        JsonObject root = Load();
        if (root["mods"] is JsonObject mods && mods.Remove(id))
        {
            Save(root);
        }
    }

    /// <summary>Does the file hold settings or key binds for the mod?</summary>
    public bool HasSettings(string id)
        => Load()["mods"] is JsonObject mods && mods[id] is JsonObject e && (e["settings"] is JsonObject { Count: > 0 } || e["keys"] is JsonObject { Count: > 0 });

    private JsonObject Load()
    {
        if (!File.Exists(Path))
        {
            return [];
        }

        byte[] bytes = File.ReadAllBytes(Path);
        if (bytes.Length == 0)
        {
            return [];
        }

        try
        {
            JsonNode? node = JsonNode.Parse(bytes, null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return node as JsonObject ?? throw new GearStateException("gear_mods.json is not a JSON object; the manager leaves it alone. Fix or delete the file.");
        }
        catch (JsonException ex)
        {
            throw new GearStateException("gear_mods.json cannot be read (" + ex.Message + "); the manager leaves it alone. Fix or delete the file.");
        }
    }

    private void Save(JsonObject root)
    {
        string tmp = Path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            root.WriteTo(writer);
        }

        File.Move(tmp, Path, overwrite: true);
    }
}

/// <summary>gear_mods.json is in a state the manager does not dare to change.</summary>
public sealed class GearStateException(string message) : Exception(message);
