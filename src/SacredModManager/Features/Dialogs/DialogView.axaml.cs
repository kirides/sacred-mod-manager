using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SacredModManager.Features.Dialogs;

/// <summary>The question or notice drawn over the window; the safe choice gets the focus when it opens.</summary>
public sealed partial class DialogView : UserControl
{
    private DialogViewModel? _watched;

    public DialogView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnDialogChanged;
        }

        _watched = DataContext as DialogViewModel;
        if (_watched is not null)
        {
            _watched.PropertyChanged += OnDialogChanged;
        }
    }

    private void OnDialogChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DialogViewModel.IsOpen) && _watched is { IsOpen: true } dialog)
        {
            // the safe choice has the focus: the secondary button when there is one
            Dispatcher.UIThread.Post(() =>
            {
                Button? target = dialog.HasSecondary ? this.FindControl<Button>("DialogSecondary")
                    : this.FindControl<Button>(dialog.Destructive ? "DialogPrimaryDanger" : "DialogPrimaryAccent");
                target?.Focus();
            }, DispatcherPriority.Background);
        }
    }

    private void OnDialogPrimary(object? sender, RoutedEventArgs e) => _watched?.Answer(true);

    private void OnDialogSecondary(object? sender, RoutedEventArgs e) => _watched?.Answer(false);
}
