using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SacredModManager.Features.ModList;
using SacredModManager.Features.Shell;

namespace SacredModManager.Tooling;

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

        // the detail pane with its expanders open, and the banner for a running game
        vm.SectionIndex = 0;
        foreach (Expander e in window.GetVisualDescendants().OfType<Expander>())
        {
            e.IsExpanded = true;
        }

        vm.GameRunning = true;
        await Task.Delay(500);
        Save(window, Path.Combine(directory, "06-expanders-running-light.png"));
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        await Task.Delay(500);
        Save(window, Path.Combine(directory, "06-expanders-running-dark.png"));

        // the version history dialog of a mod that has a changelog
        if (vm.AllItems.FirstOrDefault(i => i.HasHistory) is { } withHistory)
        {
            Task history = vm.ShowHistoryAsync(withHistory);
            await Task.Delay(700);
            Save(window, Path.Combine(directory, "06-version-history-dark.png"));
            vm.Dialog.Answer(true);
            await history;
        }
        vm.GameRunning = false;
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;

        // the picture viewer
        ModItemViewModel? withImages = vm.AllItems.FirstOrDefault(i => i.GalleryCount > 1);
        if (withImages is not null)
        {
            vm.OpenGallery(withImages);
            await Task.Delay(1200);
            Save(window, Path.Combine(directory, "07-gallery-light.png"));
            vm.Gallery.Next();
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            await Task.Delay(800);
            Save(window, Path.Combine(directory, "07-gallery-dark.png"));
            vm.Gallery.Close();
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        }

        // the other kinds: plugins and tools
        vm.SectionIndex = 1;
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        foreach ((int tab, string name) in new[] { (2, "plugins"), (3, "tools") })
        {
            vm.Browse.CategoryIndex = tab;
            await Task.Delay(500);
            Save(window, Path.Combine(directory, $"09-browse-{name}-dark.png"));
        }

        vm.Browse.CategoryIndex = 0;

        // what a gear-mod-manager:// link opens (each dialog is answered with "no")
        int n = 0;
        foreach (string link in Program.ScreenshotLinks)
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            Task handling = vm.HandleLinkAsync(link);
            await Task.Delay(1500);
            Save(window, Path.Combine(directory, $"08-link-{++n}-dark.png"));
            vm.Dialog.Answer(false);
            await handling;
        }

        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;

        // "Update all": the confirmation lists every mod that changes (answered with "no")
        if (vm.UpdateCount > 0)
        {
            vm.SectionIndex = 0;
            Task updating = vm.UpdateAllCommand.ExecuteAsync(null);
            await Task.Delay(800);
            Save(window, Path.Combine(directory, "10-update-all-light.png"));
            foreach (Expander e in window.GetVisualDescendants().OfType<Expander>().Where(e => e.Header is "Details"))
            {
                e.IsExpanded = true;
            }

            await Task.Delay(500);
            Save(window, Path.Combine(directory, "10-update-all-details-light.png"));
            vm.Dialog.Answer(false);
            await updating;
        }

        // a mod that recommends another one: its details, and the install dialog with the opt-in box (answered with "no")
        if (vm.AllItems.FirstOrDefault(i => !i.IsInstalled && i.Recommendations.Any(r => !r.Met)) is { } recommending)
        {
            window.Width = 1600;
            window.Height = 900;
            vm.SectionIndex = 1;
            vm.Browse.Selected = recommending;
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            await Task.Delay(700);
            Save(window, Path.Combine(directory, "11-recommended-details-dark.png"));
            Task installing = vm.InstallAsync(recommending);
            await Task.Delay(800);
            Save(window, Path.Combine(directory, "11-recommended-dialog-dark.png"));
            vm.Dialog.Answer(false);
            await installing;
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
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
