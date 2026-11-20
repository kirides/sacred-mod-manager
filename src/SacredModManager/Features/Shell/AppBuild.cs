using System.Reflection;
using SacredModManager.Core.App;

namespace SacredModManager.Features.Shell;

/// <summary>The version stamp of this build: <c>nightly-YYYYMMDD-sha7</c> from the nightly workflow, a version like 1.2.3 from a release, "dev" from source.</summary>
public static class AppBuild
{
    public static string Stamp { get; } = Read();

    public static BuildInfo Info { get; } = BuildInfo.Parse(Stamp);

    private static string Read()
    {
        string? v = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        int plus = v?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        return string.IsNullOrWhiteSpace(v) ? "dev" : plus > 0 ? v![..plus] : v!;
    }
}
