using Avalonia;
using SacredModManager.Core.App;
using SacredModManager.Core.Links;
using SacredModManager.Tooling;

namespace SacredModManager;

internal static class Program
{
    /// <summary>Set by <c>--screenshot &lt;dir&gt;</c>: the window is filled with demo data, a PNG of each page is saved there and the app exits.</summary>
    public static string? ScreenshotDirectory { get; private set; }

    /// <summary>Set by <c>--game &lt;folder&gt;</c>: the game folder to use instead of the saved one (tests use a copy).</summary>
    public static string? GameOverride { get; private set; }

    /// <summary>Set by <c>--source &lt;address&gt;</c> (repeatable): sources used instead of the saved ones, nothing is saved.</summary>
    public static List<string> SourceOverrides { get; } = [];

    /// <summary>Set by <c>--link &lt;gear-mod-manager:// link&gt;</c> (repeatable, screenshot mode): the link is handled and the dialog it opens is saved as a PNG.</summary>
    public static List<string> ScreenshotLinks { get; } = [];

    /// <summary>The manager's own folders (shared by the app and the single-instance lock).</summary>
    public static AppPaths Paths { get; } = new();

    /// <summary>The <c>gear-mod-manager://</c> link or the mod archive file (zip, tar, tar.gz, tar.zst) this process was started with, if any.</summary>
    public static string? StartupLink { get; private set; }

    /// <summary>The single-instance lock and listener of this process (null in the special modes).</summary>
    public static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // "--selftest <index address>": headless check of the file, archive, Lua and JSON work (also run against the published AOT exe)
        if (args.Length >= 2 && args[0] == "--selftest")
        {
            return Tooling.SelfTest.RunAsync(args[1]).GetAwaiter().GetResult();
        }

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
                case "--link":
                    ScreenshotLinks.Add(args[i + 1]);
                    break;
            }
        }

        Core.App.GameLocator.SuppressProcessCheck = ScreenshotDirectory is not null;
        StartupLink = ScreenshotDirectory is null ? LaunchTarget.FirstTarget(args) : null; // --screenshot --link handles links itself

        // one manager per player: a second start (for example by the system with a link) hands its link to the first one and ends
        bool normal = ScreenshotDirectory is null && GameOverride is null && SourceOverrides.Count == 0;
        if (normal)
        {
            SingleInstance instance = SingleInstance.Acquire(Paths.Data, StartupLink ?? ManagerLink.Scheme + "://activate");
            if (instance.Role == InstanceRole.Forwarded)
            {
                instance.Dispose();
                return 0;
            }

            Instance = instance;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Instance?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        AppBuilder builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        // screenshot mode: popups are drawn inside the window, so the window's own renderer captures them
        return ScreenshotDirectory is null ? builder : builder.With(new Win32PlatformOptions { OverlayPopups = true });
    }
}
