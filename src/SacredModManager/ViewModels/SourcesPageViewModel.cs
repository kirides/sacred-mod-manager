using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SacredModManager.Core.Index;

namespace SacredModManager.ViewModels;

/// <summary>One index that was read while loading (a source or an index it points to).</summary>
public sealed class IndexRowViewModel
{
    public required string Title { get; init; }
    public required string Address { get; init; }
    public string Detail { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public int Depth { get; init; }
    public string ModsText { get; init; } = string.Empty;
    public string Via { get; init; } = string.Empty;
    public bool IsRoot => Depth == 0;
    public bool HasError => Error.Length > 0;
    public bool HasDetail => Detail.Length > 0;
    public bool HasVia => Via.Length > 0;
    public Thickness Indent => new(Depth * 28, 0, 0, 0);
}

/// <summary>The Sources page: the indexes the player added, and what was read from them (including indexes they point to).</summary>
public sealed partial class SourcesPageViewModel(MainWindowViewModel app) : ObservableObject
{
    public ObservableCollection<string> Addresses { get; } = [];
    public ObservableCollection<IndexRowViewModel> Indexes { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];

    [ObservableProperty]
    public partial string NewAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AddError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedAddress { get; set; } = string.Empty;

    public bool HasAddError => AddError.Length > 0;
    public bool HasNotes => Notes.Count > 0;
    public bool HasIndexes => Indexes.Count > 0;

    partial void OnAddErrorChanged(string value) => OnPropertyChanged(nameof(HasAddError));

    public void Reload()
    {
        Addresses.Clear();
        foreach (string s in app.Settings.Sources)
        {
            Addresses.Add(s);
        }

        Indexes.Clear();
        Notes.Clear();
        if (app.Catalog is { } catalog)
        {
            foreach (LoadedIndex i in catalog.Indexes)
            {
                Indexes.Add(new IndexRowViewModel
                {
                    Title = i.Name.Length > 0 ? i.Name : i.Url.IsFile ? Path.GetFileName(i.Url.LocalPath) : i.Url.Host,
                    Address = i.Url.IsFile ? i.Url.LocalPath : i.Url.AbsoluteUri,
                    Detail = string.Join("  -  ", new[] { i.Author, i.Description }.Where(x => x.Length > 0)),
                    Error = i.Error ?? string.Empty,
                    Depth = i.Depth,
                    ModsText = i.Error is not null ? string.Empty : i.ModCount == 1 ? "1 mod" : i.ModCount + " mods",
                    Via = i.ParentUrl is null ? string.Empty : "listed by " + (i.ParentUrl.IsFile ? Path.GetFileName(i.ParentUrl.LocalPath) : i.ParentUrl.Host + i.ParentUrl.AbsolutePath),
                });
            }

            foreach (string w in catalog.Warnings)
            {
                Notes.Add(w);
            }
        }

        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(HasIndexes));
    }

    [RelayCommand]
    private async Task Add()
    {
        if (!SourceUrl.TryParse(NewAddress, out Uri? uri, out string? error))
        {
            AddError = error ?? "This is not a valid address.";
            return;
        }

        string text = uri!.IsFile ? uri.LocalPath : uri.AbsoluteUri;
        if (app.Settings.Sources.Any(s => string.Equals(s, text, StringComparison.OrdinalIgnoreCase)))
        {
            AddError = "This source is already in the list.";
            return;
        }

        AddError = string.Empty;
        app.Settings.Sources.Add(text);
        app.SaveSettings();
        NewAddress = string.Empty;
        Reload();
        await app.ReloadCatalogAsync();
    }

    [RelayCommand]
    private async Task Remove(string address)
    {
        if (app.Settings.Sources.RemoveAll(s => s == address) > 0)
        {
            app.SaveSettings();
            Reload();
            await app.ReloadCatalogAsync();
        }
    }

    [RelayCommand]
    private async Task ResetDefault()
    {
        app.Settings.Sources.Clear();
        app.Settings.Sources.Add(Core.App.AppSettings.DefaultSource);
        app.SaveSettings();
        Reload();
        await app.ReloadCatalogAsync();
    }

    [RelayCommand]
    private Task Refresh() => app.ReloadCatalogAsync();
}
