using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;

namespace SacredModManager.ViewModels;

/// <summary>A requirement of a mod and whether it is met.</summary>
public sealed record RequirementViewModel(string Id, string Name, string State, bool Met);

/// <summary>What a mod's buttons do; implemented by the main view model.</summary>
public interface IModActions
{
    Task ToggleAsync(ModItemViewModel item);
    Task InstallAsync(ModItemViewModel item);
    Task RemoveAsync(ModItemViewModel item);
    Task RestoreAsync(ModItemViewModel item);
    void OpenFolder(ModItemViewModel item);
}

/// <summary>One mod as the player sees it: what is installed and what the sources offer, merged by id.</summary>
public sealed partial class ModItemViewModel : ObservableObject
{
    private readonly IModActions _actions;

    private static readonly Color[] Hues =
    [
        Color.Parse("#3B6EA5"), Color.Parse("#4F8A6B"), Color.Parse("#8A6A3B"), Color.Parse("#7A5A9A"),
        Color.Parse("#9A5A5A"), Color.Parse("#4A8A94"), Color.Parse("#7F8A3F"), Color.Parse("#8A5A82"),
    ];

    public ModItemViewModel(string id, IModActions actions)
    {
        _actions = actions;
        Id = id;
        PlaceholderBrush = new SolidColorBrush(Hues[(int)((uint)StableHash(id) % (uint)Hues.Length)]);
        Name = id;
        Initials = MakeInitials(id);
    }

    public string Id { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Author { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    /// <summary>The installed version, or the available one for a mod that is not installed.</summary>
    [ObservableProperty]
    public partial string Version { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AvailableVersion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TagsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SourceName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    public partial bool IsAvailable { get; set; }

    [ObservableProperty]
    public partial bool UpdateAvailable { get; set; }

    [ObservableProperty]
    public partial bool IsManual { get; set; }

    [ObservableProperty]
    public partial bool HasModifiedFiles { get; set; }

    [ObservableProperty]
    public partial string ModifiedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasMissingRequirements { get; set; }

    [ObservableProperty]
    public partial string MissingText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    [ObservableProperty]
    public partial int BackupCount { get; set; }

    public string Initials { get; }
    public IBrush PlaceholderBrush { get; }

    public InstalledMod? Installed { get; private set; }
    public CatalogMod? Catalog { get; private set; }

    public ObservableCollection<RequirementViewModel> Requirements { get; } = [];
    public ObservableCollection<SettingDecl> Settings { get; } = [];
    public ObservableCollection<KeyDecl> Keys { get; } = [];
    public IReadOnlyList<string> Tags { get; private set; } = [];

    public bool HasThumbnail => Thumbnail is not null;
    public bool HasBackup => BackupCount > 0;
    public bool HasSource => SourceName.Length > 0;
    public bool HasTags => TagsText.Length > 0;
    public bool HasRequirements => Requirements.Count > 0;
    public bool HasSettings => Settings.Count > 0;
    public bool HasKeys => Keys.Count > 0;
    public bool HasDescription => Description.Length > 0;
    public bool HasAuthor => Author.Length > 0;
    public bool HasVersion => Version.Length > 0;
    public bool CanInstall => IsAvailable && !IsInstalled;
    public bool CanUpdate => IsAvailable && IsInstalled && UpdateAvailable;
    public bool CanRemove => IsInstalled;
    public bool NotInstalled => !IsInstalled;

    /// <summary>"0.4.0" or "0.4.0 → 0.5.0".</summary>
    public string VersionText => !IsInstalled ? Version : UpdateAvailable ? Version + "  →  " + AvailableVersion : Version;

    public string StatusText => !IsInstalled ? "Not installed" : HasError ? "Cannot be loaded" : IsEnabled ? "On" : "Off";

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));

    partial void OnBackupCountChanged(int value) => OnPropertyChanged(nameof(HasBackup));

    partial void OnSourceNameChanged(string value) => OnPropertyChanged(nameof(HasSource));

    partial void OnTagsTextChanged(string value) => OnPropertyChanged(nameof(HasTags));

    partial void OnIsInstalledChanged(bool value) => RaiseDerived();

    partial void OnIsAvailableChanged(bool value) => RaiseDerived();

    partial void OnUpdateAvailableChanged(bool value) => RaiseDerived();

    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    partial void OnHasErrorChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    partial void OnVersionChanged(string value) => RaiseDerived();

    partial void OnDescriptionChanged(string value) => OnPropertyChanged(nameof(HasDescription));

    partial void OnAuthorChanged(string value) => OnPropertyChanged(nameof(HasAuthor));

    [RelayCommand]
    private Task Toggle() => _actions.ToggleAsync(this);

    [RelayCommand]
    private Task Install() => _actions.InstallAsync(this);

    [RelayCommand]
    private Task Remove() => _actions.RemoveAsync(this);

    [RelayCommand]
    private Task Restore() => _actions.RestoreAsync(this);

    [RelayCommand]
    private void OpenFolder() => _actions.OpenFolder(this);

    /// <summary>Makes the checkbox state follow the model again after a toggle that was refused.</summary>
    public void ResyncEnabled() => OnPropertyChanged(nameof(IsEnabled));

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(NotInstalled));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasVersion));
    }

    /// <summary>Takes over the current state of the installed copy and of the catalog entry (either may be null).</summary>
    public void Update(InstalledMod? installed, CatalogMod? catalog, IReadOnlySet<string> installedIds)
    {
        Installed = installed;
        Catalog = catalog;
        IsInstalled = installed is not null;
        IsAvailable = catalog is not null;
        ModManifest? m = installed?.Manifest;

        Name = m?.DisplayName ?? catalog?.DisplayName ?? installed?.DisplayName ?? Id;
        Author = m?.Author is { Length: > 0 } a ? a : catalog?.Author ?? string.Empty;
        Description = m?.Description is { Length: > 0 } d ? d : catalog?.Description ?? string.Empty;
        AvailableVersion = catalog?.Version ?? string.Empty;
        Version = installed is not null ? installed.Version : AvailableVersion;
        UpdateAvailable = installed is not null && catalog is not null && VersionCompare.IsNewer(catalog.Version, installed.Version);
        SourceName = catalog?.IndexName ?? string.Empty;
        Tags = catalog?.Tags ?? [];
        TagsText = string.Join(", ", Tags);
        IsManual = installed is not null && installed.Tracked is null;
        IsEnabled = installed?.Enabled ?? false;
        HasError = installed?.Error is not null;
        ErrorText = installed?.Error ?? string.Empty;
        int modified = installed?.ModifiedFiles ?? 0;
        HasModifiedFiles = modified > 0;
        ModifiedText = modified == 1 ? "1 file changed since install" : modified + " files changed since install";

        Requirements.Clear();
        IEnumerable<string> requires = m?.Requires ?? catalog?.Requires ?? [];
        int missing = 0;
        var missingNames = new List<string>();
        foreach (string req in requires)
        {
            bool met = installedIds.Contains(req);
            if (!met)
            {
                missing++;
                missingNames.Add(req);
            }

            Requirements.Add(new RequirementViewModel(req, req, met ? "installed" : "missing", met));
        }

        HasMissingRequirements = installed is not null && missing > 0;
        MissingText = HasMissingRequirements ? "Needs " + string.Join(", ", missingNames) : string.Empty;

        Settings.Clear();
        foreach (SettingDecl s in m?.Settings ?? [])
        {
            Settings.Add(s);
        }

        Keys.Clear();
        foreach (KeyDecl k in m?.Keys ?? [])
        {
            Keys.Add(k);
        }

        OnPropertyChanged(nameof(HasRequirements));
        OnPropertyChanged(nameof(HasSettings));
        OnPropertyChanged(nameof(HasKeys));
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(VersionText));
    }

    private static int StableHash(string s)
    {
        int h = 17;
        foreach (char c in s)
        {
            h = unchecked((h * 31) + c);
        }

        return h & int.MaxValue;
    }

    private static string MakeInitials(string id)
    {
        string[] parts = id.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => string.Concat(parts[0][0], parts[1][0]).ToUpperInvariant(),
        };
    }
}
