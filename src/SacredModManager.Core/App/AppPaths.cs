namespace SacredModManager.Core.App;

/// <summary>Where the manager keeps its own files. <c>SACRED_MODMANAGER_HOME</c> moves all of them (tests, a portable copy).</summary>
public sealed class AppPaths
{
    public AppPaths(string? home = null)
    {
        home ??= Environment.GetEnvironmentVariable("SACRED_MODMANAGER_HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Settings = Path.Combine(appData, "SacredModManager", "settings.json");
            Data = Path.Combine(local, "SacredModManager");
        }
        else
        {
            Settings = Path.Combine(home, "settings.json");
            Data = Path.Combine(home, "data");
        }
    }

    public string Settings { get; }

    /// <summary>Downloaded packages, thumbnails, backups.</summary>
    public string Data { get; }

    public string Packages => Path.Combine(Data, "packages");
    public string Thumbnails => Path.Combine(Data, "thumbnails");
    public string Backups => Path.Combine(Data, "backups");
}
