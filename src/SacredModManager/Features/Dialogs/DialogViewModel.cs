using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SacredModManager.Features.Dialogs;

/// <summary>The answer to a dialog.</summary>
public readonly record struct DialogResult(bool Primary, bool Checked);

/// <summary>A question or notice shown inside the window (one at a time). Escape answers with the secondary button.</summary>
public sealed partial class DialogViewModel : ObservableObject
{
    private TaskCompletionSource<DialogResult>? _pending;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PrimaryText { get; set; } = "OK";

    /// <summary>Empty: a notice with only the primary button.</summary>
    [ObservableProperty]
    public partial string SecondaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CheckText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CheckValue { get; set; }

    [ObservableProperty]
    public partial bool Destructive { get; set; }

    public ObservableCollection<string> Lines { get; } = [];

    public bool HasMessage => Message.Length > 0;
    public bool HasSecondary => SecondaryText.Length > 0;
    public bool HasCheck => CheckText.Length > 0;
    public bool HasLines => Lines.Count > 0;
    public bool PrimaryIsAccent => !Destructive;

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnSecondaryTextChanged(string value) => OnPropertyChanged(nameof(HasSecondary));

    partial void OnCheckTextChanged(string value) => OnPropertyChanged(nameof(HasCheck));

    partial void OnDestructiveChanged(bool value) => OnPropertyChanged(nameof(PrimaryIsAccent));

    public Task<DialogResult> ShowAsync(string title, string message, string primary, string secondary = "", string check = "", bool checkValue = false, bool destructive = false, IEnumerable<string>? lines = null)
    {
        _pending?.TrySetResult(new DialogResult(false, false));
        Title = title;
        Message = message;
        PrimaryText = primary;
        SecondaryText = secondary;
        CheckText = check;
        CheckValue = checkValue;
        Destructive = destructive;
        Lines.Clear();
        foreach (string l in lines ?? [])
        {
            Lines.Add(l);
        }

        OnPropertyChanged(nameof(HasLines));
        var tcs = new TaskCompletionSource<DialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;
        IsOpen = true;
        return tcs.Task;
    }

    public void Answer(bool primary)
    {
        IsOpen = false;
        TaskCompletionSource<DialogResult>? tcs = _pending;
        _pending = null;
        tcs?.TrySetResult(new DialogResult(primary, CheckValue));
    }
}
