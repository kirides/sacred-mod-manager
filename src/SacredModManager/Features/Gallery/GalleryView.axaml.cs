using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace SacredModManager.Features.Gallery;

/// <summary>The picture viewer: a dark overlay over the window with the current picture, arrows and dots.</summary>
public sealed partial class GalleryView : UserControl
{
    private GalleryViewModel? _watched;

    public GalleryView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnGalleryChanged;
        }

        _watched = DataContext as GalleryViewModel;
        if (_watched is not null)
        {
            _watched.PropertyChanged += OnGalleryChanged;
        }
    }

    /// <summary>When the viewer opens the window takes the focus, so that the arrow keys and Esc reach it.</summary>
    private void OnGalleryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(GalleryViewModel.IsOpen) && _watched is { IsOpen: true })
        {
            Dispatcher.UIThread.Post(() => TopLevel.GetTopLevel(this)?.Focus(), DispatcherPriority.Background);
        }
    }

    /// <summary>A click beside the picture closes the viewer.</summary>
    private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => (DataContext as GalleryViewModel)?.Close();

    /// <summary>A click on the picture shows the next one and does not close the viewer.</summary>
    private void OnImagePressed(object? sender, PointerPressedEventArgs e)
    {
        (DataContext as GalleryViewModel)?.Next();
        e.Handled = true;
    }
}
