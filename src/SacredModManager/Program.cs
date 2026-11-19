using Avalonia;

namespace SacredModManager;

internal static class Program
{
    /// <summary>Set by <c>--screenshot &lt;dir&gt;</c>: the window is filled with demo data, a PNG of each page is saved there and the app exits.</summary>
    public static string? ScreenshotDirectory { get; private set; }

    /// <summary>Set by <c>--game &lt;folder&gt;</c>: the game folder to use instead of the saved one (tests use a copy).</summary>
    public static string? GameOverride { get; private set; }

    /// <summary>Set by <c>--source &lt;address&gt;</c> (repeatable): sources used instead of the saved ones, nothing is saved.</summary>
    public static List<string> SourceOverrides { get; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            switch (args[i])
            {
                case "--screenshot":
                    ScreenshotDirectory = args[i + 1];
                    break;
                case "--game":
                    GameOverride = args[i + 1];
                    break;
                case "--source":
                    SourceOverrides.Add(args[i + 1]);
                    break;
            }
        }

        Core.App.GameLocator.SuppressProcessCheck = ScreenshotDirectory is not null;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        AppBuilder builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        // screenshot mode: popups are drawn inside the window, so the window's own renderer captures them
        return ScreenshotDirectory is null ? builder : builder.With(new Win32PlatformOptions { OverlayPopups = true });
    }
}
