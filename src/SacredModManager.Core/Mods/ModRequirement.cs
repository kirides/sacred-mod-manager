namespace SacredModManager.Core.Mods;

/// <summary>
/// One entry of <c>requires</c> in an index: a plain id (<c>"sacred-dsoal"</c>), or an id with a minimum version
/// (<c>"sacred-gear&gt;=0.4.1"</c>, spaces around the operator allowed). A plain id keeps working everywhere.
/// </summary>
public readonly record struct ModRequirement(string Id, string? MinVersion)
{
    public const int MaxVersionLength = 64;

    /// <summary>Parses one entry; false when it is neither a valid id nor <c>id&gt;=version</c>.</summary>
    public static bool TryParse(string? text, out ModRequirement requirement)
    {
        requirement = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        int at = text.IndexOf(">=", StringComparison.Ordinal);
        if (at < 0)
        {
            string id = text.Trim();
            if (!ModIdRules.IsValid(id))
            {
                return false;
            }

            requirement = new ModRequirement(id, null);
            return true;
        }

        string name = text[..at].Trim();
        string version = text[(at + 2)..].Trim();
        if (!ModIdRules.IsValid(name) || !IsValidVersion(version))
        {
            return false;
        }

        requirement = new ModRequirement(name, version);
        return true;
    }

    /// <summary>True when the version a player has satisfies the minimum. A version that is not known (not installed from an index, or an index without one) cannot be checked and counts as met.</summary>
    public static bool IsMet(string? minVersion, string? actualVersion)
        => string.IsNullOrWhiteSpace(minVersion) || string.IsNullOrWhiteSpace(actualVersion) || VersionCompare.Compare(actualVersion, minVersion) >= 0;

    public bool IsMetBy(string? actualVersion) => IsMet(MinVersion, actualVersion);

    private static bool IsValidVersion(string v)
    {
        if (v.Length == 0 || v.Length > MaxVersionLength || !char.IsAsciiLetterOrDigit(v[0]))
        {
            return false;
        }

        foreach (char c in v)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+'))
            {
                return false;
            }
        }

        return true;
    }
}
