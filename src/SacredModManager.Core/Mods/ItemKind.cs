namespace SacredModManager.Core.Mods;

/// <summary>
/// What an index entry is. <see cref="Mod"/>: a Sacred Gear mod (a folder with mod.lua that goes to <c>&lt;game&gt;\mods\</c>, switched on and off in
/// <c>gear_mods.json</c>). <see cref="Plugin"/>: files for the game folder itself, such as <c>ddraw.dll</c> of Sacred-Bild or <c>SacredGear.dll</c>.
/// <see cref="Tool"/>: a program that runs on its own (for example the class editor), unpacked into the manager's tools folder.
/// </summary>
public enum ItemKind
{
    Mod,
    Plugin,
    Tool,
}

/// <summary>Names of the kinds as the index spells them and as the player reads them.</summary>
public static class ItemKinds
{
    /// <summary>"mod", "plugin" or "tool" (case-insensitive; empty means mod); null for anything else.</summary>
    public static ItemKind? Parse(string? text)
    {
        return text?.Trim().ToLowerInvariant() switch
        {
            null or "" or "mod" or "mods" => ItemKind.Mod,
            "plugin" or "plugins" => ItemKind.Plugin,
            "tool" or "tools" => ItemKind.Tool,
            _ => null,
        };
    }

    public static string ToJson(ItemKind kind) => kind switch { ItemKind.Plugin => "plugin", ItemKind.Tool => "tool", _ => "mod" };

    public static string Singular(ItemKind kind) => kind switch { ItemKind.Plugin => "Plugin", ItemKind.Tool => "Tool", _ => "Mod" };

    public static string Plural(ItemKind kind) => kind switch { ItemKind.Plugin => "Plugins", ItemKind.Tool => "Tools", _ => "Mods" };
}
