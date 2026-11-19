using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SacredModManager.Core.Mods;
using SacredModManager.Features.Shell;

namespace SacredModManager.Features.ModList;

/// <summary>The kinds of list the page shows.</summary>
public enum ModsPageKind
{
    Installed,
    Browse,
}

/// <summary>The Installed or the Browse page: a searchable, filterable list of mods with a detail pane.</summary>
public sealed partial class ModsPageViewModel : ObservableObject
{
    private static readonly string[] InstalledFilters = ["All", "On", "Off", "Updates available", "Problems", "Installed by hand"];
    private static readonly string[] BrowseFilters = ["All", "Not installed", "Installed", "Updates available"];
    public const string AllTags = "All categories";

    private readonly MainWindowViewModel _app;

    public ModsPageViewModel(MainWindowViewModel app, ModsPageKind kind)
    {
        _app = app;
        Kind = kind;
        Filters = kind == ModsPageKind.Installed ? InstalledFilters : BrowseFilters;
        Tags.Add(AllTags);
        SelectedTag = AllTags;
    }

    public ModsPageKind Kind { get; }
    public MainWindowViewModel App => _app;
    public bool IsBrowse => Kind == ModsPageKind.Browse;
    public string Title => IsBrowse ? "Browse" : "Installed";
    public IReadOnlyList<string> Filters { get; }

    public ObservableCollection<ModItemViewModel> Visible { get; } = [];
    public ObservableCollection<string> Tags { get; } = [];

    /// <summary>All / Mods / Plugins / Tools, each with the number of items of that kind.</summary>
    public ObservableCollection<string> CategoryTabs { get; } = ["All", "Mods", "Plugins", "Tools"];

    [ObservableProperty]
    public partial int CategoryIndex { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int FilterIndex { get; set; }

    [ObservableProperty]
    public partial string SelectedTag { get; set; }

    [ObservableProperty]
    public partial ModItemViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial string CountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmptyTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmptyHint { get; set; } = string.Empty;

    public bool IsEmpty => Visible.Count == 0;
    public bool HasSelection => Selected is not null;
    public bool NoSelection => Selected is null;

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnFilterIndexChanged(int value) => Refresh();

    partial void OnCategoryIndexChanged(int value) => Refresh();

    partial void OnSelectedTagChanged(string value) => Refresh();

    partial void OnSelectedChanged(ModItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(NoSelection));
    }

    public void ClearSearch() => SearchText = string.Empty;

    /// <summary>Rebuilds the visible list from all mods (keeps the selection when the mod is still shown).</summary>
    public void Refresh()
    {
        string? keep = Selected?.Id;
        RefreshTags();
        string query = SearchText.Trim();
        List<ModItemViewModel> pool = _app.AllItems.Where(i => IsBrowse ? i.IsAvailable : i.IsInstalled).ToList();
        UpdateCategoryTabs(pool);
        IEnumerable<ModItemViewModel> items = CategoryIndex switch
        {
            1 => pool.Where(i => i.Kind == ItemKind.Mod),
            2 => pool.Where(i => i.Kind == ItemKind.Plugin),
            3 => pool.Where(i => i.Kind == ItemKind.Tool),
            _ => pool,
        };
        if (query.Length > 0)
        {
            items = items.Where(i => Matches(i, query));
        }

        if (IsBrowse && SelectedTag != AllTags)
        {
            items = items.Where(i => i.Tags.Contains(SelectedTag));
        }

        items = Kind == ModsPageKind.Installed ? FilterInstalled(items) : FilterBrowse(items);
        List<ModItemViewModel> list = items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        Visible.Clear();
        foreach (ModItemViewModel i in list)
        {
            Visible.Add(i);
        }

        Selected = list.FirstOrDefault(i => i.Id == keep) ?? list.FirstOrDefault();
        int total = CategoryIndex switch { 1 => pool.Count(i => i.IsMod), 2 => pool.Count(i => i.IsPlugin), 3 => pool.Count(i => i.IsTool), _ => pool.Count };
        string noun = CategoryIndex switch { 1 => "mods", 2 => "plugins", 3 => "tools", _ => "items" };
        CountText = list.Count == total ? $"{total} {noun}" : $"{list.Count} of {total} {noun}";
        UpdateEmpty(total, query.Length > 0 || FilterIndex != 0 || (IsBrowse && SelectedTag != AllTags));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void UpdateCategoryTabs(List<ModItemViewModel> pool)
    {
        string[] titles =
        [
            "All  " + pool.Count,
            "Mods  " + pool.Count(i => i.IsMod),
            "Plugins  " + pool.Count(i => i.IsPlugin),
            "Tools  " + pool.Count(i => i.IsTool),
        ];
        for (int i = 0; i < titles.Length; i++)
        {
            if (CategoryTabs[i] != titles[i])
            {
                int keep = CategoryIndex;
                CategoryTabs[i] = titles[i];
                if (CategoryIndex != keep)
                {
                    CategoryIndex = keep; // replacing a tab's text must not move the selection
                }
            }
        }
    }

    private void UpdateEmpty(int total, bool filtered)
    {
        if (total > 0 && filtered)
        {
            EmptyTitle = "Nothing matches";
            EmptyHint = "Clear the search or the filters to see all mods.";
        }
        else if (IsBrowse)
        {
            EmptyTitle = _app.IsLoadingCatalog ? "Loading the mod lists..." : "No mods to show";
            EmptyHint = _app.IsLoadingCatalog ? string.Empty : "Add a source on the Sources page, or check your internet connection and refresh (F5).";
        }
        else
        {
            EmptyTitle = _app.HasGame ? "No mods installed yet" : "Choose your Sacred folder first";
            EmptyHint = _app.HasGame ? "Open Browse to install mods, or drop a mod zip onto this window." : "Open Settings and select the folder with Sacred.exe.";
        }
    }

    private void RefreshTags()
    {
        if (!IsBrowse)
        {
            return;
        }

        List<string> tags = _app.AllItems.Where(i => i.IsAvailable).SelectMany(i => i.Tags).Distinct().Order(StringComparer.Ordinal).ToList();
        if (Tags.Count - 1 == tags.Count && tags.Zip(Tags.Skip(1)).All(p => p.First == p.Second))
        {
            return;
        }

        string current = SelectedTag;
        Tags.Clear();
        Tags.Add(AllTags);
        foreach (string t in tags)
        {
            Tags.Add(t);
        }

        SelectedTag = tags.Contains(current) ? current : AllTags;
    }

    private static bool Matches(ModItemViewModel i, string query)
        => i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
           || i.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
           || i.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
           || i.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase)
           || i.TagsText.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private IEnumerable<ModItemViewModel> FilterInstalled(IEnumerable<ModItemViewModel> items) => FilterIndex switch
    {
        1 => items.Where(i => i.IsEnabled && !i.HasError),
        2 => items.Where(i => !i.IsEnabled),
        3 => items.Where(i => i.UpdateAvailable),
        4 => items.Where(i => i.HasError || i.HasMissingRequirements),
        5 => items.Where(i => i.IsManual),
        _ => items,
    };

    private IEnumerable<ModItemViewModel> FilterBrowse(IEnumerable<ModItemViewModel> items) => FilterIndex switch
    {
        1 => items.Where(i => !i.IsInstalled),
        2 => items.Where(i => i.IsInstalled),
        3 => items.Where(i => i.UpdateAvailable),
        _ => items,
    };
}
