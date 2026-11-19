using System.Runtime.InteropServices;

namespace SacredModManager.Core.App;

/// <summary>The platform the manager runs on, spelled like the .NET runtime identifiers that index entries use in <c>platforms</c>.</summary>
public static class PlatformInfo
{
    /// <summary>"win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-arm64" ...</summary>
    public static string CurrentRid { get; } = Compute();

    private static string Compute()
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : "other";
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => "other",
        };
        return os + "-" + arch;
    }

    /// <summary>Does an entry with these <c>platforms</c> (empty: every platform) fit <paramref name="rid"/>? "win" matches every win-* too.</summary>
    public static bool Matches(IEnumerable<string>? platforms, string rid)
    {
        if (platforms is null)
        {
            return true;
        }

        bool any = false;
        foreach (string p in platforms)
        {
            string x = p.Trim().ToLowerInvariant();
            if (x.Length == 0)
            {
                continue;
            }

            any = true;
            if (x == rid || rid.StartsWith(x + "-", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return !any;
    }
}
