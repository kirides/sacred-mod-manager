using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SacredModManager.Core.App;
using SacredModManager.Services;
using SacredModManager.ViewModels;
using SacredModManager.Views;

namespace SacredModManager;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = new AppPaths();
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
            window.Opened += async (_, _) =>
            {
                await vm.StartAsync();
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
