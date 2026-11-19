namespace SacredModManager.Core.App;

/// <summary>The program file that a link or a launcher should start: for an AppImage the <c>.AppImage</c> itself, otherwise the running program.</summary>
public static class AppExecutable
{
    /// <summary>
    /// Inside an AppImage the running program lives in a temporary mount that disappears when the app ends, so the file to register is the
    /// AppImage (the runtime tells it in <c>APPIMAGE</c>). Null when the manager runs through the dotnet host (a build folder).
    /// </summary>
    public static string? Path()
    {
        string? image = Environment.GetEnvironmentVariable("APPIMAGE");
        if (OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(image) && File.Exists(image))
        {
            return image;
        }

        string? exe = Environment.ProcessPath;
        return exe is null || string.Equals(System.IO.Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe) ? null : exe;
    }
}
