using Avalonia.Controls;

namespace SacredModManager.Views;

public sealed partial class ModsPageView : UserControl
{
    public ModsPageView() => InitializeComponent();

    /// <summary>Puts the cursor in the search box (Ctrl+F).</summary>
    public void FocusSearch()
    {
        TextBox? box = this.FindControl<TextBox>("SearchBox");
        box?.Focus();
        box?.SelectAll();
    }
}
