using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using SacredModManager.ViewModels;

namespace SacredModManager.Services;

/// <summary>
/// <c>--screenshot &lt;dir&gt;</c>: saves a PNG of every page in the light and the dark theme (the images of the README), then the app exits.
/// Use with <c>--game</c> (a copy of a game folder) and <c>--source</c> (an index, local files work) so that nothing real is touched.
/// </summary>
internal static class ScreenshotRunner
{
    public static async Task RunAsync(Window window, MainWindowViewModel vm, string directory)
    {
        Directory.CreateDirectory(directory);
        // let the catalog and thumbnails arrive
        for (int i = 0; i < 60 && (vm.IsLoadingCatalog || vm.AllItems.Count == 0); i++)
        {
            await Task.Delay(250);
        }

        await Task.Delay(1500);
        string[] names = ["installed", "browse", "sources", "settings"];
        foreach ((string variant, ThemeVariant theme) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
        {
            Application.Current!.RequestedThemeVariant = theme;
            for (int page = 0; page < names.Length; page++)
            {
                vm.SectionIndex = page;
                await Task.Delay(500);
                Save(window, Path.Combine(directory, $"{page + 1:00}-{names[page]}-{variant}.png"));
            }
        }

        // a dialog
        vm.SectionIndex = 0;
        ModItemViewModel? item = vm.AllItems.FirstOrDefault(i => i.IsInstalled);
        if (item is not null)
        {
            _ = vm.Dialog.ShowAsync("Remove " + item.Name + "?", "The files of the mod are removed. A backup copy is kept and you can restore it.", "Remove", "Keep it", "Also delete the data and settings the mod saved", false, true);
            await Task.Delay(500);
            Save(window, Path.Combine(directory, "05-dialog-light.png"));
            vm.Dialog.Answer(false);
        }
    }

    private static void Save(Window window, string path)
    {
        Dispatcher.UIThread.RunJobs();
        PixelSize size = new((int)window.Bounds.Width, (int)window.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
}
