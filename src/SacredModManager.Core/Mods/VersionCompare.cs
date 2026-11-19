using System.Globalization;

namespace SacredModManager.Core.Mods;

/// <summary>Compares mod version strings ("0.4.0", "1.2", "v2.0-beta"): numeric parts numerically, text parts ordinally, missing parts count as 0.</summary>
public static class VersionCompare
{
    public static int Compare(string? a, string? b)
    {
        string[] pa = Split(a);
        string[] pb = Split(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            string x = i < pa.Length ? pa[i] : "0";
            string y = i < pb.Length ? pb[i] : "0";
            bool nx = ulong.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out ulong vx);
            bool ny = ulong.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out ulong vy);
            int c = nx && ny ? vx.CompareTo(vy) : nx ? 1 : ny ? -1 : string.CompareOrdinal(x, y);
            if (c != 0)
            {
                return Math.Sign(c);
            }
        }

        return 0;
    }

    /// <summary>True when <paramref name="available"/> is a newer version than <paramref name="installed"/>.</summary>
    public static bool IsNewer(string? available, string? installed)
        => !string.IsNullOrWhiteSpace(available) && Compare(available, installed) > 0;

    private static string[] Split(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
        {
            return [];
        }

        string s = v.Trim();
        if (s.Length > 1 && (s[0] is 'v' or 'V') && char.IsAsciiDigit(s[1]))
        {
            s = s[1..];
        }

        return s.Split(['.', '-', '_', '+', ' '], StringSplitOptions.RemoveEmptyEntries);
    }
}
