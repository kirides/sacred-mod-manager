using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;

namespace SacredModManager.Features.ModList;

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
    void OpenGallery(ModItemViewModel item);
    void LaunchTool(ModItemViewModel item);
    Task CopyLinkAsync(ModItemViewModel item);
    Task MoveAsync(ModItemViewModel item, int delta);
    Task MoveToAsync(ModItemViewModel item, int index);
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

    /// <summary>The whole changelog of the available version, newest first (empty until it is loaded or when the mod has none).</summary>
    public IReadOnlyList<ChangelogEntry> ChangelogEntries { get; private set; } = [];

    /// <summary>The entries to show: what is new since the installed version, or the newest entry when nothing is installed or up to date.</summary>
    public IReadOnlyList<ChangelogEntry> WhatsNew
    {
        get
        {
            if (ChangelogEntries.Count == 0)
            {
                return [];
            }

            IReadOnlyList<ChangelogEntry> news = UpdateAvailable
                ? Changelog.Between(ChangelogEntries, Installed?.Version, AvailableVersion)
                : Changelog.Between(ChangelogEntries, null, IsInstalled ? Version : AvailableVersion);
            return news;
        }
    }

    public bool HasWhatsNew => WhatsNew.Count > 0;

    public string WhatsNewHeader => UpdateAvailable ? "What's new since " + Installed?.Version : IsInstalled ? "What's in this version" : "What's new in the latest version";

    public string WhatsNewText => Changelog.Format(WhatsNew);

    /// <summary>Every version of the changelog, newest first, for the "Version history" section.</summary>
    public bool HasHistory => ChangelogEntries.Count > 0;

    public string HistoryHeader => ChangelogEntries.Count == 1 ? "Version history (1 version)" : "Version history (" + ChangelogEntries.Count + " versions)";

    public string HistoryText => Changelog.Format(ChangelogEntries);

    public void SetChangelog(IReadOnlyList<ChangelogEntry> entries)
    {
        ChangelogEntries = entries;
        RaiseWhatsNew();
    }

    private void RaiseWhatsNew()
    {
        OnPropertyChanged(nameof(WhatsNew));
        OnPropertyChanged(nameof(HasWhatsNew));
        OnPropertyChanged(nameof(WhatsNewHeader));
        OnPropertyChanged(nameof(WhatsNewText));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HistoryHeader));
        OnPropertyChanged(nameof(HistoryText));
    }


    [ObservableProperty]
    public partial int BackupCount { get; set; }

    /// <summary>Place in the load order, from 1; 0 for what has none (plugins, tools, mods that cannot load).</summary>
    [ObservableProperty]
    public partial int LoadPosition { get; set; }

    /// <summary>The list is sorted by load order: the move buttons are shown.</summary>
    [ObservableProperty]
    public partial bool OrderMode { get; set; }

    /// <summary>The row can be dragged to a new place (sorted by load order, nothing searched or filtered, the mod has a place).</summary>
    [ObservableProperty]
    public partial bool CanDrag { get; set; }

    [ObservableProperty]
    public partial bool CanMoveUp { get; set; }

    [ObservableProperty]
    public partial bool CanMoveDown { get; set; }

    /// <summary>An installed mod Sacred Gear skips (a requirement is missing or loops): no place in the load order.</summary>
    [ObservableProperty]
    public partial bool WontLoad { get; set; }

    public string WontLoadText => HasMissingRequirements ? "Won't load: " + MissingText.ToLowerInvariant() : "Won't load: its requirements loop";

    public void NotifyWontLoadText() => OnPropertyChanged(nameof(WontLoadText));

    public bool ShowOrder => OrderMode && LoadPosition > 0;
    public string PositionText => LoadPosition > 0 ? LoadPosition.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    partial void OnLoadPositionChanged(int value)
    {
        OnPropertyChanged(nameof(ShowOrder));
        OnPropertyChanged(nameof(PositionText));
    }

    partial void OnOrderModeChanged(bool value) => OnPropertyChanged(nameof(ShowOrder));

    public string Initials { get; }
    public IBrush PlaceholderBrush { get; }

    public InstalledMod? Installed { get; private set; }
    public CatalogMod? Catalog { get; private set; }

    public ObservableCollection<RequirementViewModel> Requirements { get; } = [];
    public ObservableCollection<SettingDecl> Settings { get; } = [];
    public ObservableCollection<KeyDecl> Keys { get; } = [];
    public IReadOnlyList<string> Tags { get; private set; } = [];

    public bool HasThumbnail => Thumbnail is not null;
    public int GalleryCount => Catalog?.Images.Count ?? 0;
    public bool HasGallery => GalleryCount > 0;
    public string GalleryText => GalleryCount == 1 ? "View picture" : GalleryCount + " pictures";
    public ItemKind Kind { get; private set; }
    public bool IsMod => Kind == ItemKind.Mod;
    public bool IsPlugin => Kind == ItemKind.Plugin;
    public bool IsTool => Kind == ItemKind.Tool;
    public bool NotMod => Kind != ItemKind.Mod;
    public string KindText => ItemKinds.Singular(Kind);

    /// <summary>Only an installed mod that loads has an On/Off switch (plugins and tools are simply there).</summary>
    public bool HasToggle => IsInstalled && IsMod && !HasError;

    public bool ShowToggleCard => IsInstalled && IsMod;
    public bool ShowOnChip => IsInstalled && IsMod && IsEnabled && !HasError;
    public bool ShowFilesCard => IsInstalled && NotMod;
    public bool CanLaunch => IsInstalled && IsTool && !string.IsNullOrEmpty(Installed?.Tracked?.Launch) && !HasError;
    public string FilesText { get; private set; } = string.Empty;
    public string ErrorTitle => IsMod ? "This mod cannot be loaded" : "There is a problem with this " + KindText.ToLowerInvariant();
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

    /// <summary>The "Installed" chip of a Browse card: only when there is neither an Install nor an Update button in its place.</summary>
    public bool ShowInstalledChip => !CanInstall && !CanUpdate;
    public bool CanRemove => IsInstalled;
    public bool NotInstalled => !IsInstalled;

    /// <summary>"0.4.0" or "0.4.0 → 0.5.0".</summary>
    public string VersionText => !IsInstalled ? Version : UpdateAvailable ? Version + "  →  " + AvailableVersion : Version;

    public string StatusText => !IsInstalled ? "Not installed" : HasError ? (IsMod ? "Cannot be loaded" : "Problem") : !IsMod ? "Installed" : IsEnabled ? "On" : "Off";

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));

    partial void OnBackupCountChanged(int value) => OnPropertyChanged(nameof(HasBackup));

    partial void OnSourceNameChanged(string value) => OnPropertyChanged(nameof(HasSource));

    partial void OnTagsTextChanged(string value) => OnPropertyChanged(nameof(HasTags));

    partial void OnIsInstalledChanged(bool value) => RaiseDerived();

    partial void OnIsAvailableChanged(bool value) => RaiseDerived();

    partial void OnUpdateAvailableChanged(bool value) => RaiseDerived();

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowOnChip));
    }

    partial void OnHasErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasToggle));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(ShowOnChip));
    }

    partial void OnVersionChanged(string value) => RaiseDerived();

    partial void OnDescriptionChanged(string value) => OnPropertyChanged(nameof(HasDescription));

    partial void OnAuthorChanged(string value) => OnPropertyChanged(nameof(HasAuthor));

    [RelayCommand]
    private Task Toggle() => _actions.ToggleAsync(this);

    public Task MoveToAsync(int index) => _actions.MoveToAsync(this, index);

    [RelayCommand]
    private Task MoveUp() => _actions.MoveAsync(this, -1);

    [RelayCommand]
    private Task MoveDown() => _actions.MoveAsync(this, 1);

    [RelayCommand]
    private Task Install() => _actions.InstallAsync(this);

    [RelayCommand]
    private Task Remove() => _actions.RemoveAsync(this);

    [RelayCommand]
    private Task Restore() => _actions.RestoreAsync(this);

    [RelayCommand]
    private void OpenFolder() => _actions.OpenFolder(this);

    [RelayCommand]
    private void OpenGallery() => _actions.OpenGallery(this);

    [RelayCommand]
    private void Launch() => _actions.LaunchTool(this);

    [RelayCommand]
    private Task CopyLink() => _actions.CopyLinkAsync(this);

    /// <summary>Makes the checkbox state follow the model again after a toggle that was refused.</summary>
    public void ResyncEnabled() => OnPropertyChanged(nameof(IsEnabled));

    private void RaiseDerived()
    {
        RaiseWhatsNew();
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(ShowInstalledChip));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(NotInstalled));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasVersion));
    }

    /// <summary>Takes over the current state of the installed copy and of the catalog entry (either may be null).</summary>
    public void Update(InstalledMod? installed, CatalogMod? catalog, IReadOnlySet<string> installedIds, IReadOnlyDictionary<string, string>? availableNames = null,
        IReadOnlyDictionary<string, string>? installedVersions = null, IReadOnlyDictionary<string, CatalogMod>? catalogById = null)
    {
        Installed = installed;
        Catalog = catalog;
        IsInstalled = installed is not null;
        IsAvailable = catalog is not null;
        ModManifest? m = installed?.Manifest;

        Kind = catalog?.Kind ?? installed?.Kind ?? ItemKind.Mod;
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
        FilesText = BuildFilesText(installed);
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
            string? min = catalog?.MinVersions.GetValueOrDefault(req);
            string? have = installedVersions?.GetValueOrDefault(req);
            bool present = installedIds.Contains(req);
            bool met = present && ModRequirement.IsMet(min, have);
            CatalogMod? offeredMod = catalogById?.GetValueOrDefault(req);
            string? offeredVersion = offeredMod is null ? null : offeredMod.Version.Length > 0 ? offeredMod.Version : offeredMod.PackageVersion;
            bool offered = availableNames is not null && availableNames.ContainsKey(req);
            string needs = min is null ? req : req + " " + min + " or newer";
            if (!met)
            {
                missing++;
                missingNames.Add(needs);
            }

            string state = met ? "installed"
                : present ? (offered && ModRequirement.IsMet(min, offeredVersion) ? "installed (" + have + "), will be updated with it" : "installed (" + have + "), but " + min + " or newer is needed")
                : offered ? "will be installed with it" : "not available";
            string name = offered ? availableNames![req] : req;
            Requirements.Add(new RequirementViewModel(req, min is null ? name : name + " " + min + "+", state, met));
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

        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(IsMod));
        OnPropertyChanged(nameof(IsPlugin));
        OnPropertyChanged(nameof(IsTool));
        OnPropertyChanged(nameof(NotMod));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(HasToggle));
        OnPropertyChanged(nameof(ShowToggleCard));
        OnPropertyChanged(nameof(ShowOnChip));
        OnPropertyChanged(nameof(ShowFilesCard));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(FilesText));
        OnPropertyChanged(nameof(ErrorTitle));
        OnPropertyChanged(nameof(GalleryCount));
        OnPropertyChanged(nameof(HasGallery));
        OnPropertyChanged(nameof(GalleryText));
        OnPropertyChanged(nameof(HasRequirements));
        OnPropertyChanged(nameof(HasSettings));
        OnPropertyChanged(nameof(HasKeys));
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(VersionText));
    }

    private static string BuildFilesText(InstalledMod? installed)
    {
        if (installed is null || installed.Kind == ItemKind.Mod || installed.Tracked is null)
        {
            return string.Empty;
        }

        int n = installed.Tracked.Files.Count;
        string where = installed.Kind == ItemKind.Plugin ? "in your Sacred folder" : "in " + installed.Directory;
        return (n == 1 ? "1 file " : n + " files ") + where;
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
