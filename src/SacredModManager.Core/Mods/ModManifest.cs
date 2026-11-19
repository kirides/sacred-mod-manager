namespace SacredModManager.Core.Mods;

/// <summary>One declared setting of a mod (<c>settings</c> in mod.lua); only what the manager shows.</summary>
public sealed record SettingDecl(string Id, string Type, string Label, string DefaultText, string? Section, string? Tooltip);

/// <summary>One declared key bind of a mod (<c>keys</c> in mod.lua).</summary>
public sealed record KeyDecl(string Id, string Label, string Default);

/// <summary>The metadata of a mod read from its <c>mod.lua</c> (see Sacred Gear's docs, "mod.lua").</summary>
public sealed class ModManifest
{
    public required string Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public double Priority { get; init; }
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>The mod's own default: <c>enabled = false</c> in mod.lua means off until the player switches it on.</summary>
    public bool DefaultEnabled { get; init; } = true;

    public IReadOnlyList<SettingDecl> Settings { get; init; } = [];
    public IReadOnlyList<KeyDecl> Keys { get; init; } = [];

    /// <summary>The name to show: <see cref="Name"/>, else the id.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

/// <summary>What Sacred Gear accepts as a mod id: letters, digits, '_' and '-'.</summary>
public static class ModIdRules
{
    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64)
        {
            return false;
        }

        foreach (char c in id)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
