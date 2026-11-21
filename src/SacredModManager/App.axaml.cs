using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SacredModManager.Core.App;
using SacredModManager.Features.Shell;
using SacredModManager.Tooling;

namespace SacredModManager;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppPaths paths = Program.Paths;
            bool screenshot = Program.ScreenshotDirectory is not null;
            AppSettings settings = SettingsFile.Load(paths.Settings);
            if (Program.GameOverride is { } game)
            {
                settings.GameFolder = game;
            }

            if (Program.SourceOverrides.Count > 0)
            {
                settings.Sources = [.. Program.SourceOverrides];
            }

            // with --game / --source / --screenshot nothing is written to the saved settings
            bool persist = !screenshot && Program.GameOverride is null && Program.SourceOverrides.Count == 0;
            var vm = new MainWindowViewModel(settings, paths, persist ? paths.Settings : null);
            var window = new MainWindow { DataContext = vm, Width = Math.Max(980, settings.WindowWidth), Height = Math.Max(600, settings.WindowHeight) };
            desktop.MainWindow = window;
            // links that arrive while the manager runs (a second start forwards them): bring the window up and ask the player
            Program.Instance?.Listen(message => Dispatcher.UIThread.Post(async () =>
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                window.Activate();
                await vm.HandleLaunchTargetAsync(message);
            }));
            window.Opened += async (_, _) =>
            {
                await vm.StartAsync();
                if (Program.StartupLink is { } link)
                {
                    await vm.HandleLaunchTargetAsync(link);
                }

                if (Program.ScreenshotDirectory is { } dir)
                {
                    await ScreenshotRunner.RunAsync(window, vm, dir);
                    desktop.Shutdown();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
