using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace SacredModManager.Features.ModList;

public sealed partial class ModsPageView : UserControl
{
    private const string DragFormatPrefix = "gearmm-load-order:";

    public ModsPageView()
    {
        InitializeComponent();
        ListBox list = InstalledList;
        list.AddHandler(PointerPressedEvent, OnPointerPressed);
        list.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        list.AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropLine.IsVisible = false);
        list.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>Puts the cursor in the search box (Ctrl+F).</summary>
    public void FocusSearch()
    {
        TextBox? box = this.FindControl<TextBox>("SearchBox");
        box?.Focus();
        box?.SelectAll();
    }

    /// <summary>A press on a row's grip starts dragging that mod (load order view only: the grip is shown only then).</summary>
    private async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Border? grip = source as Border ?? source.FindAncestorOfType<Border>();
        while (grip is not null && grip.Name != "Grip")
        {
            grip = grip.FindAncestorOfType<Border>();
        }

        if (grip?.DataContext is not ModItemViewModel { CanDrag: true } item)
        {
            return;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(DragFormatPrefix + item.Id));
        e.Handled = true;
        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            DropLine.IsVisible = false;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool ours = DraggedId(e) is not null && DataContext is ModsPageViewModel { SortByLoadOrder: true };
        e.DragEffects = ours ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        if (!ours)
        {
            DropLine.IsVisible = false;
            return;
        }

        (int slot, double y) = Slot(e);
        DropLine.Margin = new Thickness(8, Math.Max(0, y - 1.5), 8, 0);
        DropLine.IsVisible = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropLine.IsVisible = false;
        string? id = DraggedId(e);
        if (id is null || DataContext is not ModsPageViewModel page)
        {
            return;
        }

        e.Handled = true;
        List<ModItemViewModel> movable = page.Visible.Where(i => i.CanDrag).ToList();
        int from = movable.FindIndex(i => i.Id == id);
        if (from < 0)
        {
            return;
        }

        (int slot, _) = Slot(e);
        int index = from < slot ? slot - 1 : slot; // the dragged row leaves its place first
        if (index != from)
        {
            await movable[from].MoveToAsync(index);
        }

        page.Selected = page.Visible.FirstOrDefault(i => i.Id == id) ?? page.Selected; // the row under the pointer must not take the selection
    }

    private static string? DraggedId(DragEventArgs e)
        => e.DataTransfer.TryGetText() is { } text && text.StartsWith(DragFormatPrefix, StringComparison.Ordinal) ? text[DragFormatPrefix.Length..] : null;

    /// <summary>The drop place: how many draggable rows are above the pointer (by their middle), and the y of the line between rows.</summary>
    private (int Slot, double Y) Slot(DragEventArgs e)
    {
        ListBox list = InstalledList;
        double pointer = e.GetPosition(list).Y;
        int slot = 0;
        double line = 0;
        if (DataContext is ModsPageViewModel page)
        {
            for (int i = 0; i < page.Visible.Count; i++)
            {
                if (!page.Visible[i].CanDrag || list.ContainerFromIndex(i) is not Control row)
                {
                    continue;
                }

                double top = row.TranslatePoint(new Point(0, 0), list)?.Y ?? 0;
                if (slot == 0)
                {
                    line = top;
                }

                if (top + row.Bounds.Height / 2 < pointer)
                {
                    slot++;
                    line = top + row.Bounds.Height;
                }
            }
        }

        return (slot, line);
    }
}
