using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SacredModManager.Features.Dialogs;

/// <summary>The answer to a dialog.</summary>
public readonly record struct DialogResult(bool Primary, bool Checked);

/// <summary>One row of a list dialog: a name, what happens to it (<see cref="Badge"/>, <see cref="Versions"/>) and the technical details hidden behind a button.</summary>
public sealed record DialogEntry(string Title, string Badge, string Versions, IReadOnlyList<string> Details, string Note = "", string Changes = "")
{
    public bool HasDetails => Details.Count > 0;
    public bool HasNote => Note.Length > 0;
    public bool HasChanges => Changes.Length > 0;
    public string DetailsText => string.Join(Environment.NewLine, Details);
}

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

    /// <summary>Light markdown shown in a scrollable area (the version history); empty for none.</summary>
    [ObservableProperty]
    public partial string Markdown { get; set; } = string.Empty;

    /// <summary>A wider dialog, for longer text.</summary>
    [ObservableProperty]
    public partial bool Wide { get; set; }

    public double DialogWidth => Wide ? 760 : 560;
    public bool HasMarkdown => Markdown.Length > 0;

    partial void OnMarkdownChanged(string value) => OnPropertyChanged(nameof(HasMarkdown));

    partial void OnWideChanged(bool value) => OnPropertyChanged(nameof(DialogWidth));

    public ObservableCollection<string> Lines { get; } = [];

    public ObservableCollection<DialogEntry> Entries { get; } = [];

    public bool HasEntries => Entries.Count > 0;
    public bool HasMessage => Message.Length > 0;
    public bool HasSecondary => SecondaryText.Length > 0;
    public bool HasCheck => CheckText.Length > 0;
    public bool HasLines => Lines.Count > 0;
    public bool PrimaryIsAccent => !Destructive;

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnSecondaryTextChanged(string value) => OnPropertyChanged(nameof(HasSecondary));

    partial void OnCheckTextChanged(string value) => OnPropertyChanged(nameof(HasCheck));

    partial void OnDestructiveChanged(bool value) => OnPropertyChanged(nameof(PrimaryIsAccent));

    public Task<DialogResult> ShowAsync(string title, string message, string primary, string secondary = "", string check = "", bool checkValue = false, bool destructive = false, IEnumerable<string>? lines = null, IEnumerable<DialogEntry>? entries = null, string markdown = "", bool wide = false)
    {
        _pending?.TrySetResult(new DialogResult(false, false));
        Title = title;
        Message = message;
        PrimaryText = primary;
        SecondaryText = secondary;
        CheckText = check;
        CheckValue = checkValue;
        Destructive = destructive;
        Markdown = markdown;
        Wide = wide;
        Lines.Clear();
        foreach (string l in lines ?? [])
        {
            Lines.Add(l);
        }

        Entries.Clear();
        foreach (DialogEntry e in entries ?? [])
        {
            Entries.Add(e);
        }

        OnPropertyChanged(nameof(HasLines));
        OnPropertyChanged(nameof(HasEntries));
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
