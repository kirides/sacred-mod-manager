using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SacredModManager.Features.ModList;

namespace SacredModManager.Features.Shell;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnWindowKeyDown;
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        Activated += (_, _) => (DataContext as MainWindowViewModel)?.OnWindowActivated();
        Closed += (_, _) => (DataContext as MainWindowViewModel)?.Dispose();
        Closing += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.Settings.WindowWidth = Width;
                vm.Settings.WindowHeight = Height;
                vm.Shutdown();
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PickFolder = PickFolderAsync;
            vm.PickZip = PickZipAsync;
            vm.SetClipboard = async text =>
            {
                if (Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(text);
                }
            };
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (vm.Gallery.IsOpen && !vm.Dialog.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    vm.Gallery.Close();
                    break;
                case Key.Left or Key.Up or Key.PageUp:
                    vm.Gallery.Previous();
                    break;
                case Key.Right or Key.Down or Key.PageDown or Key.Space:
                    vm.Gallery.Next();
                    break;
                case Key.Home:
                    vm.Gallery.First();
                    break;
                case Key.End:
                    vm.Gallery.Last();
                    break;
                default:
                    return;
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && vm.Dialog.IsOpen)
        {
            vm.Dialog.Answer(false);
            e.Handled = true;
        }
        else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && !vm.Dialog.IsOpen)
        {
            ModsPageView? page = vm.ShowBrowse ? this.FindControl<ModsPageView>("BrowsePage") : vm.ShowInstalled ? this.FindControl<ModsPageView>("InstalledPage") : null;
            if (page is not null)
            {
                page.FocusSearch();
                e.Handled = true;
            }
        }
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        IEnumerable<IStorageItem> files = e.DataTransfer.TryGetFiles() ?? [];
        foreach (IStorageItem item in files)
        {
            string? path = item.TryGetLocalPath();
            if (path is not null && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await vm.InstallZipAsync(path);
            }
        }
    }

    private async Task<string?> PickFolderAsync(string title, string? start)
    {
        IStorageFolder? startFolder = null;
        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start))
        {
            startFolder = await StorageProvider.TryGetFolderFromPathAsync(start);
        }

        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickZipAsync()
    {
        IReadOnlyList<IStorageFile> picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a mod zip",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Zip files") { Patterns = ["*.zip"] }],
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }
}
