using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SacredModManager.Core.App;
using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;
using SacredModManager.Features.Dialogs;
using SacredModManager.Features.Gallery;
using SacredModManager.Features.ModList;
using SacredModManager.Features.Settings;
using SacredModManager.Features.Sources;

namespace SacredModManager.Features.Shell;

/// <summary>The window: navigation, the game folder, loading, and every action on mods.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IModActions, IDisposable
{
    private readonly AppPaths _paths;
    private readonly string? _settingsPath;
    private readonly ModManagerService _service;
    private readonly Fetcher _fetcher = new();
    private readonly Dictionary<string, ModItemViewModel> _items = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly SemaphoreSlim _thumbnailGate = new(3);
    private List<InstalledMod> _installed = [];
    private IndexCatalog? _catalog;
    private ModStore? _store;
    private Func<Task>? _undo;
    private bool _busy;
    private CancellationTokenSource _catalogCts = new();

    public MainWindowViewModel(AppSettings settings, AppPaths paths, string? settingsPath)
    {
        Settings = settings;
        _paths = paths;
        _settingsPath = settingsPath;
        _service = new ModManagerService(paths, _fetcher);
        Installed = new ModsPageViewModel(this, ModsPageKind.Installed);
        Browse = new ModsPageViewModel(this, ModsPageKind.Browse);
        Sources = new SourcesPageViewModel(this);
        SettingsPage = new SettingsPageViewModel(this);
        _poll.Tick += (_, _) => PollGame();
    }

    public AppSettings Settings { get; }
    public ModManagerService Service => _service;
    public AppPaths Paths => _paths;
    public ModsPageViewModel Installed { get; }
    public ModsPageViewModel Browse { get; }
    public SourcesPageViewModel Sources { get; }
    public SettingsPageViewModel SettingsPage { get; }
    public DialogViewModel Dialog { get; } = new();
    public GalleryViewModel Gallery { get; } = new();
    public ObservableCollection<ModItemViewModel> AllItems { get; } = [];
    public IndexCatalog? Catalog => _catalog;
    public ModStore? Store => _store;

    /// <summary>Set by the window: a folder picker (title, start folder) and a zip picker.</summary>
    public Func<string, string?, Task<string?>>? PickFolder { get; set; }

    public Func<Task<string?>>? PickZip { get; set; }

    /// <summary>The window title.</summary>
    public string WindowTitle => HasGame ? "Gear Mod Manager  -  " + GameFolderName : "Gear Mod Manager";

    // ---- navigation ---------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial int SectionIndex { get; set; }

    public bool ShowInstalled => SectionIndex == 0;
    public bool ShowBrowse => SectionIndex == 1;
    public bool ShowSources => SectionIndex == 2;
    public bool ShowSettings => SectionIndex == 3;

    partial void OnSectionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ShowInstalled));
        OnPropertyChanged(nameof(ShowBrowse));
        OnPropertyChanged(nameof(ShowSources));
        OnPropertyChanged(nameof(ShowSettings));
    }

    [RelayCommand]
    private void Go(string index) => SectionIndex = int.Parse(index, System.Globalization.CultureInfo.InvariantCulture);

    // ---- game ---------------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string GameFolder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasGame { get; set; }

    [ObservableProperty]
    public partial bool GameRunning { get; set; }

    public string GameFolderName => string.IsNullOrEmpty(GameFolder) ? string.Empty : Path.GetFileName(GameFolder.TrimEnd('\\', '/'));
    public bool NoGame => !HasGame;
    public bool CanLaunch => HasGame && !GameRunning;
    public string GameStateText => !HasGame ? "No game folder" : GameRunning ? "Sacred is running" : "Sacred is not running";

    partial void OnHasGameChanged(bool value)
    {
        OnPropertyChanged(nameof(NoGame));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(GameStateText));
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnGameRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(GameStateText));
    }

    partial void OnGameFolderChanged(string value)
    {
        OnPropertyChanged(nameof(GameFolderName));
        OnPropertyChanged(nameof(WindowTitle));
    }

    /// <summary>Uses the folder (when it has Sacred.exe), saves it and reloads. Returns an error text or null.</summary>
    public async Task<string?> SetGameFolderAsync(string? folder)
    {
        if (!GameLocator.IsGameFolder(folder))
        {
            return "There is no Sacred.exe in that folder.";
        }

        Settings.GameFolder = Path.GetFullPath(folder!);
        SaveSettings();
        ApplyGame();
        await ReloadInstalledAsync();
        return null;
    }

    private void ApplyGame()
    {
        string? folder = GameLocator.Find(Settings.GameFolder);
        if (folder is null)
        {
            _store = null;
            GameFolder = string.Empty;
            HasGame = false;
        }
        else
        {
            _store = new ModStore(folder, _paths.Backups, ToolsRoot);
            GameFolder = folder;
            HasGame = true;
        }

        SettingsPage.Reload();
    }

    private void PollGame()
    {
        bool running = HasGame && GameLocator.IsGameRunning();
        if (running != GameRunning)
        {
            GameRunning = running;
            if (!running)
            {
                _ = ReloadInstalledAsync(); // the game may have changed gear_mods.json
            }
        }
    }

    [RelayCommand]
    private void LaunchGame()
    {
        if (!HasGame)
        {
            return;
        }

        if (GameLocator.Launch(GameFolder, out string? error))
        {
            SetStatus("Started Sacred.");
            PollGame();
        }
        else
        {
            SetStatus("Sacred could not be started: " + error, error: true);
        }
    }

    // ---- a new version of the manager ---------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool AppUpdateAvailable { get; set; }

    [ObservableProperty]
    public partial string AppUpdateText { get; set; } = string.Empty;

    private UpdateInfo? _appUpdate;

    /// <summary>Asks GitHub for a newer version of the manager (nightly builds follow the nightlies, release builds the releases); only tells, never downloads.</summary>
    public async Task CheckForAppUpdateAsync()
    {
        if (!Settings.CheckForUpdates || _settingsPath is null || AppBuild.Info.Kind == BuildKind.Development)
        {
            return;
        }

        try
        {
            UpdateInfo? update = await new UpdateChecker(_fetcher).CheckAsync(AppBuild.Info, UpdateChecker.Repository, CancellationToken.None);
            if (update is null || string.Equals(update.Tag, Settings.DismissedUpdate, StringComparison.Ordinal))
            {
                ClearAppUpdate();
                return;
            }

            _appUpdate = update;
            AppUpdateText = "A new version of Gear Mod Manager is available: " + update.Title;
            AppUpdateAvailable = true;
        }
        catch (FetchException)
        {
            // no internet, or GitHub says no: the banner is a convenience, so nothing is shown
        }
    }

    public void ClearAppUpdate()
    {
        _appUpdate = null;
        AppUpdateAvailable = false;
    }

    [RelayCommand]
    private void OpenAppUpdate()
    {
        if (_appUpdate is not null)
        {
            OpenPath(_appUpdate.Url.AbsoluteUri);
        }
    }

    [RelayCommand]
    private void DismissAppUpdate()
    {
        if (_appUpdate is not null)
        {
            Settings.DismissedUpdate = _appUpdate.Tag;
            SaveSettings();
        }

        ClearAppUpdate();
    }

    // ---- status -------------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready";

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    [ObservableProperty]
    public partial bool IsWorking { get; set; }

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial bool ProgressIndeterminate { get; set; } = true;

    [ObservableProperty]
    public partial string UndoText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoadingCatalog { get; set; }

    [ObservableProperty]
    public partial string CatalogProblemText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int InstalledCount { get; set; }

    [ObservableProperty]
    public partial int UpdateCount { get; set; }

    [ObservableProperty]
    public partial int AvailableCount { get; set; }

    public bool CanUndo => UndoText.Length > 0;
    public bool HasCatalogProblem => CatalogProblemText.Length > 0;
    public bool HasUpdates => UpdateCount > 0;
    public string UpdateAllText => UpdateCount == 1 ? "1 update" : UpdateCount + " updates";
    public string InstalledBadge => InstalledCount > 0 ? InstalledCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
    public string BrowseBadge => UpdateCount > 0 ? UpdateCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
    public bool HasBrowseBadge => UpdateCount > 0;

    partial void OnUndoTextChanged(string value) => OnPropertyChanged(nameof(CanUndo));

    partial void OnCatalogProblemTextChanged(string value) => OnPropertyChanged(nameof(HasCatalogProblem));

    partial void OnInstalledCountChanged(int value) => OnPropertyChanged(nameof(InstalledBadge));

    partial void OnUpdateCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(UpdateAllText));
        OnPropertyChanged(nameof(BrowseBadge));
        OnPropertyChanged(nameof(HasBrowseBadge));
    }

    public void SetStatus(string text, bool error = false)
    {
        StatusText = text;
        StatusIsError = error;
    }

    private void SetUndo(string text, Func<Task>? undo)
    {
        _undo = undo;
        UndoText = undo is null ? string.Empty : text;
    }

    [RelayCommand]
    private async Task Undo()
    {
        Func<Task>? undo = _undo;
        SetUndo(string.Empty, null);
        if (undo is not null)
        {
            await undo();
        }
    }

    private IProgress<(long Done, long Total)> Progress() => new Progress<(long Done, long Total)>(p =>
    {
        if (p.Total > 0)
        {
            ProgressIndeterminate = false;
            ProgressValue = 100.0 * p.Done / p.Total;
        }
        else
        {
            ProgressIndeterminate = true;
        }
    });

    /// <summary>Runs one action at a time; a second request while one runs is ignored with a note.</summary>
    private async Task<bool> RunExclusiveAsync(Func<Task> action)
    {
        if (_busy)
        {
            SetStatus("Please wait until the current action is done.");
            return false;
        }

        _busy = true;
        IsWorking = true;
        ProgressIndeterminate = true;
        try
        {
            await action();
            return true;
        }
        finally
        {
            IsWorking = false;
            _busy = false;
        }
    }

    // ---- start and reload ---------------------------------------------------------------------------------------

    public async Task StartAsync()
    {
        ApplyTheme();
        ApplyGame();
        PollGame();
        _poll.Start();
        EnsureLinkRegistration();
        SettingsPage.Reload();
        _ = CheckForAppUpdateAsync();
        await ReloadInstalledAsync();
        await ReloadCatalogAsync();
    }

    [RelayCommand]
    private async Task Refresh()
    {
        await ReloadInstalledAsync();
        await ReloadCatalogAsync();
    }

    public async Task ReloadInstalledAsync()
    {
        if (_store is null)
        {
            _installed = [];
        }
        else
        {
            try
            {
                _installed = await _store.ScanAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus("The mods folder cannot be read: " + ex.Message, error: true);
                _installed = [];
            }
        }

        Rebuild();
    }

    public async Task ReloadCatalogAsync()
    {
        await _catalogCts.CancelAsync();
        _catalogCts = new CancellationTokenSource();
        CancellationToken ct = _catalogCts.Token;
        IsLoadingCatalog = true;
        Browse.Refresh();
        try
        {
            _catalog = await _service.LoadCatalogAsync(Settings.Sources, ct);
            CatalogProblemText = _catalog.Warnings.Count == 0 ? string.Empty
                : _catalog.Warnings.Count == 1 ? "A mod source reported a problem: " + _catalog.Warnings[0]
                : _catalog.Warnings.Count + " notes from the mod sources (see Sources).";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            IsLoadingCatalog = false;
        }

        Rebuild();
        Sources.Reload();
        _ = LoadThumbnailsAsync(ct);
        SetStatus(_catalog.Mods.Count == 0 ? "No mods found in the sources." : $"{_catalog.Mods.Count} mods available from {_catalog.Indexes.Count(i => i.Error is null)} source(s).");
    }

    /// <summary>Merges what is installed with what the sources offer into one item per mod id.</summary>
    private void Rebuild()
    {
        Dictionary<string, InstalledMod> installed = [];
        foreach (InstalledMod m in _installed)
        {
            installed.TryAdd(m.Id, m);
        }

        Dictionary<string, CatalogMod> catalog = _catalog?.Mods.ToDictionary(m => m.Id, StringComparer.Ordinal) ?? [];
        var ids = new HashSet<string>(installed.Keys, StringComparer.Ordinal);
        ids.UnionWith(catalog.Keys);
        var installedIds = installed.Where(p => p.Value.Manifest is not null || p.Value.Kind != ItemKind.Mod).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        var availableNames = catalog.ToDictionary(p => p.Key, p => p.Value.DisplayName, StringComparer.Ordinal);
        foreach (string stale in _items.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _items.Remove(stale);
            AllItems.Remove(AllItems.First(i => i.Id == stale));
        }

        foreach (string id in ids)
        {
            if (!_items.TryGetValue(id, out ModItemViewModel? item))
            {
                item = new ModItemViewModel(id, this);
                _items[id] = item;
                AllItems.Add(item);
            }

            item.Update(installed.GetValueOrDefault(id), catalog.GetValueOrDefault(id), installedIds, availableNames);
            item.BackupCount = _store?.Backups(id).Count ?? 0;
        }

        InstalledCount = AllItems.Count(i => i.IsInstalled);
        UpdateCount = AllItems.Count(i => i.UpdateAvailable);
        AvailableCount = AllItems.Count(i => i.IsAvailable);
        Installed.Refresh();
        Browse.Refresh();
    }

    private async Task LoadThumbnailsAsync(CancellationToken ct)
    {
        var tasks = new List<Task>();
        foreach (ModItemViewModel item in AllItems.Where(i => i.Catalog is { } c && (c.ThumbnailUrl is not null || c.ThumbnailPath is not null) && i.Thumbnail is null).ToList())
        {
            CatalogMod mod = item.Catalog!;
            tasks.Add(Task.Run(async () =>
            {
                await _thumbnailGate.WaitAsync(ct);
                try
                {
                    byte[]? bytes = await _service.GetThumbnailAsync(mod, allowDownload: true, ct);
                    if (bytes is not null && DecodeBitmap(bytes, 400) is { } bitmap)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => item.Thumbnail = bitmap);
                    }
                }
                catch (OperationCanceledException)
                {
                    // the list was reloaded meanwhile
                }
                finally
                {
                    _thumbnailGate.Release();
                }
            }, ct));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer load
        }
    }

    /// <summary>Decodes a picture; <paramref name="width"/> above 0 decodes it at that width (thumbnails), 0 at full size.</summary>
    internal static Bitmap? DecodeBitmap(byte[] bytes, int width)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            return width > 0 ? Bitmap.DecodeToWidth(ms, width) : new Bitmap(ms);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    // ---- actions on mods ----------------------------------------------------------------------------------------

    private async Task<bool> EnsureGameClosedAsync()
    {
        if (!HasGame || _store is null)
        {
            await Dialog.ShowAsync("No game folder", "Choose the folder with Sacred.exe in Settings first.", "OK");
            SectionIndex = 3;
            return false;
        }

        PollGame();
        if (GameRunning)
        {
            await Dialog.ShowAsync("Sacred is running", "Close the game first. It keeps its mod files open and would overwrite the On/Off choices you make here.", "OK");
            return false;
        }

        return true;
    }

    public async Task ToggleAsync(ModItemViewModel item)
    {
        if (_store is null || item.Installed?.Manifest is not { } manifest)
        {
            item.ResyncEnabled();
            return;
        }

        bool desired = !item.Installed.Enabled;
        if (!await EnsureGameClosedAsync())
        {
            item.ResyncEnabled();
            return;
        }

        try
        {
            if (desired)
            {
                foreach (string req in manifest.Requires)
                {
                    ModItemViewModel? dep = _items.GetValueOrDefault(req);
                    if (dep?.Installed is { Manifest: not null } && !dep.Installed.Enabled)
                    {
                        _store.State.SetEnabled(req, true);
                    }
                }
            }

            _store.State.SetEnabled(manifest.Id, desired);
        }
        catch (Exception ex) when (ex is GearStateException or IOException or UnauthorizedAccessException)
        {
            await Dialog.ShowAsync("The choice could not be saved", ex.Message, "OK");
            item.ResyncEnabled();
            return;
        }

        await ReloadInstalledAsync();
        List<string> dependents = _items.Values.Where(i => i.IsInstalled && i.Requirements.Any(r => r.Id == manifest.Id)).Select(i => i.Name).ToList();
        SetStatus(desired ? item.Name + " is on." : item.Name + " is off." + (dependents.Count > 0 ? " In the game, " + string.Join(", ", dependents) + " stay off while it is off." : string.Empty));
        SetUndo("Undo", async () =>
        {
            if (await EnsureGameClosedAsync())
            {
                _store.State.SetEnabled(manifest.Id, !desired);
                await ReloadInstalledAsync();
                SetStatus(item.Name + (desired ? " is off again." : " is on again."));
            }
        });
    }

    public async Task InstallAsync(ModItemViewModel item)
    {
        if (item.Catalog is not { } mod || !await EnsureGameClosedAsync())
        {
            return;
        }

        await InstallManyAsync([mod], item.UpdateAvailable ? "Update " + item.Name : "Install " + item.Name);
    }

    [RelayCommand]
    private async Task UpdateAll()
    {
        List<CatalogMod> updates = AllItems.Where(i => i.CanUpdate && i.Catalog is not null).Select(i => i.Catalog!).ToList();
        if (updates.Count == 0 || !await EnsureGameClosedAsync())
        {
            return;
        }

        await InstallManyAsync(updates, "Update all");
    }

    private async Task InstallManyAsync(List<CatalogMod> wanted, string title)
    {
        ModStore store = _store!;
        InstallPlan plan = ModManagerService.Plan(wanted, _catalog?.Mods ?? [], _installed.Where(m => m.Manifest is not null || m.Kind != ItemKind.Mod).Select(m => m.Id));
        List<string> extra = plan.Install.Where(m => !plan.Wanted.Contains(m.Id)).Select(m => m.DisplayName).ToList();
        bool hasPlugin = plan.Install.Any(m => m.Kind == ItemKind.Plugin);
        bool hasTool = plan.Install.Any(m => m.Kind == ItemKind.Tool);
        if (extra.Count > 0 || plan.Missing.Count > 0 || wanted.Count > 1 || hasPlugin || hasTool)
        {
            var lines = new List<string>();
            lines.AddRange(plan.Install.Select(m => (plan.Wanted.Contains(m.Id) ? string.Empty : "needed: ") + (m.Kind == ItemKind.Mod ? string.Empty : m.KindLabel() + ": ")
                                                    + m.DisplayName + (m.Version.Length > 0 ? "  " + m.Version : string.Empty) + WhereText(m)));
            lines.AddRange(plan.Missing.Select(m => "not available anywhere: " + m));
            string message = extra.Count > 0 ? "These will be installed. " + extra.Count + " of them are needed by the others." : "These will be installed or updated.";
            if (hasPlugin)
            {
                message += " A plugin copies files into your Sacred folder; a file that is already there is saved and comes back when the plugin is removed.";
            }

            if (hasTool)
            {
                message += " A tool is a program: it is unpacked into " + ToolsRoot + " and starts on its own.";
            }

            if (plan.Missing.Count > 0)
            {
                message += " Some required items are not offered by your sources; the game will switch the mods that need them off.";
            }

            DialogResult answer = await Dialog.ShowAsync(title, message, "Install", "Cancel", lines: lines);
            if (!answer.Primary)
            {
                return;
            }
        }

        List<BatchItem> results = [];
        await RunExclusiveAsync(async () =>
        {
            var status = new Progress<string>(s => SetStatus(s + "..."));
            results = await _service.InstallPlanAsync(store, plan, status, Progress(), CancellationToken.None);
        });
        await ReloadInstalledAsync();

        List<BatchItem> failed = results.Where(r => r.Error is not null).ToList();
        List<BatchItem> ok = results.Where(r => r.Outcome is not null).ToList();
        if (failed.Count > 0)
        {
            await Dialog.ShowAsync("Some mods could not be installed", "The others were installed.", "OK", lines: failed.Select(f => f.Id + ": " + f.Error));
        }

        if (ok.Count > 0)
        {
            string names = ok.Count == 1 ? _items.GetValueOrDefault(ok[0].Id)?.Name ?? ok[0].Id : ok.Count + " mods";
            string modified = ok.Sum(o => o.Outcome!.ReplacedModified) is > 0 and var n ? $" ({n} changed file(s) were replaced; the old copy is in the backup)" : string.Empty;
            SetStatus((ok.Any(o => o.Outcome!.PreviousVersion is not null) ? "Updated " : "Installed ") + names + "." + modified, error: failed.Count > 0);
            var undoList = ok.Select(o => o.Outcome!).ToList();
            bool canUndo = undoList.All(o => o.Backup is not null || o.PreviousVersion is null); // an updated plugin or tool has no copy to go back to
            SetUndo("Undo", !canUndo ? null : async () =>
            {
                if (!await EnsureGameClosedAsync())
                {
                    return;
                }

                foreach (InstallOutcome o in undoList)
                {
                    if (o.Backup is not null)
                    {
                        store.Restore(o.Backup);
                    }
                    else
                    {
                        InstalledMod? current = (await store.ScanAsync()).FirstOrDefault(m => m.Id == o.Id);
                        if (current is not null)
                        {
                            store.Uninstall(current, removeData: false, forgetSettings: false);
                        }
                    }
                }

                await ReloadInstalledAsync();
                SetStatus("Undone.");
            });
        }
        else if (failed.Count > 0)
        {
            SetStatus("Nothing was installed.", error: true);
        }

    }

    public async Task RemoveAsync(ModItemViewModel item)
    {
        if (item.Installed is not { } mod || _store is null || !await EnsureGameClosedAsync())
        {
            return;
        }

        bool manual = mod.Tracked is null && mod.Kind == ItemKind.Mod;
        List<string> dependents = _items.Values.Where(i => i.IsInstalled && i.Id != item.Id && i.Requirements.Any(r => r.Id == item.Id)).Select(i => i.Name).ToList();
        string message = mod.Kind switch
        {
            ItemKind.Plugin => "The files of the plugin are removed from your Sacred folder; files it replaced come back as they were.",
            ItemKind.Tool => "The files of the tool are removed.",
            _ => manual
                ? "This mod was not installed by the manager, so its whole folder is removed. A backup copy is kept and you can restore it."
                : "The files of the mod are removed. A backup copy is kept and you can restore it.",
        };
        if (dependents.Count > 0)
        {
            message += " These mods need it and will stop working: " + string.Join(", ", dependents) + ".";
        }

        bool removeData = manual;
        if (Settings.AskBeforeRemove || dependents.Count > 0 || mod.Kind != ItemKind.Mod)
        {
            DialogResult answer = await Dialog.ShowAsync("Remove " + item.Name + "?", message, "Remove", "Keep it", mod.Kind switch { ItemKind.Plugin => string.Empty, ItemKind.Tool => "Also delete everything else in the tool's folder", _ => manual ? string.Empty : "Also delete the data and settings the mod saved" }, false, true);
            if (!answer.Primary)
            {
                return;
            }

            removeData = manual || (mod.Kind != ItemKind.Plugin && answer.Checked);
        }

        BackupInfo? backup = null;
        try
        {
            await RunExclusiveAsync(() =>
            {
                backup = _store.Uninstall(mod, removeData, forgetSettings: removeData);
                return Task.CompletedTask;
            });
        }
        catch (Exception ex) when (ex is StoreException or GearStateException)
        {
            await Dialog.ShowAsync("The mod could not be removed", ex.Message, "OK");
            return;
        }

        await ReloadInstalledAsync();
        SetStatus(item.Name + " was removed.");
        if (backup is not null)
        {
            SetUndo("Undo", async () =>
            {
                if (await EnsureGameClosedAsync())
                {
                    _store.Restore(backup);
                    await ReloadInstalledAsync();
                    SetStatus(item.Name + " is back.");
                }
            });
        }
    }

    public async Task RestoreAsync(ModItemViewModel item)
    {
        if (_store is null || !await EnsureGameClosedAsync())
        {
            return;
        }

        List<BackupInfo> backups = _store.Backups(item.Id);
        if (backups.Count == 0)
        {
            await Dialog.ShowAsync("No backup", "There is no earlier copy of this mod.", "OK");
            return;
        }

        BackupInfo latest = backups[0];
        string when = latest.When.ToString("g", System.Globalization.CultureInfo.CurrentCulture);
        DialogResult answer = await Dialog.ShowAsync("Restore the previous copy?",
            $"Version {(latest.Version.Length > 0 ? latest.Version : "unknown")} from {when} ({latest.Reason}) replaces the mod as it is now. The current copy is backed up first, so you can go back again.",
            "Restore", "Cancel");
        if (!answer.Primary)
        {
            return;
        }

        try
        {
            _store.Restore(latest);
        }
        catch (StoreException ex)
        {
            await Dialog.ShowAsync("Restoring failed", ex.Message, "OK");
            return;
        }

        await ReloadInstalledAsync();
        SetStatus(item.Name + " was restored to version " + latest.Version + ".");
    }

    /// <summary>The folder tools are unpacked into: the player's choice, else the manager's own tools folder.</summary>
    public string ToolsRoot => string.IsNullOrWhiteSpace(Settings.ToolsFolder) ? _paths.Tools : Settings.ToolsFolder;

    private static string WhereText(CatalogMod m) => m.Kind switch
    {
        ItemKind.Plugin when m.InstallFiles.Count > 0 => "  (" + string.Join(", ", m.InstallFiles.Select(f => f.To)) + " into the Sacred folder)",
        ItemKind.Plugin => "  (all its files into the Sacred folder)",
        ItemKind.Tool => "  (its own folder)",
        _ => string.Empty,
    };

    /// <summary>Starts an installed tool from its own folder.</summary>
    public void LaunchTool(ModItemViewModel item)
    {
        if (_store is null || item.Installed?.Tracked is not { Launch: { Length: > 0 } launch })
        {
            return;
        }

        string? exe = _store.ResolveToolLaunch(item.Id, launch);
        if (exe is null)
        {
            SetStatus(item.Name + ": the program " + launch + " is not there any more. Reinstall it.", error: true);
            return;
        }

        try
        {
            using Process? p = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = true });
            SetStatus("Started " + item.Name + ".");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            SetStatus(item.Name + " could not be started: " + ex.Message, error: true);
        }
    }

    /// <summary>Uses another folder for tools (empty: the manager's own). Tools that are already installed stay where they are listed.</summary>
    public void SetToolsFolder(string? folder)
    {
        Settings.ToolsFolder = folder?.Trim().Trim('"') ?? string.Empty;
        SaveSettings();
        ApplyGame();
    }

    public void OpenFolder(ModItemViewModel item)
    {
        if (item.Installed is { } m)
        {
            OpenPath(m.Directory);
        }
    }

    public void OpenGallery(ModItemViewModel item)
    {
        if (item.Catalog is not { } mod || mod.Images.Count == 0)
        {
            return;
        }

        IReadOnlyList<CatalogImage> images = mod.Images;
        Gallery.Open(item.Name, images.Count, async (i, ct) =>
        {
            byte[]? bytes = await _service.GetImageAsync(mod, images[i], allowDownload: true, ct);
            return bytes is null ? null : await Task.Run(() => DecodeBitmap(bytes, 0), ct);
        });
    }

    [RelayCommand]
    private void OpenModsFolder()
    {
        if (_store is not null)
        {
            Directory.CreateDirectory(_store.ModsDir);
            OpenPath(_store.ModsDir);
        }
    }

    public void OpenPath(string path)
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            SetStatus("Cannot open " + path + ": " + ex.Message, error: true);
        }
    }

    // ---- zip files ----------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task InstallZip()
    {
        string? path = PickZip is null ? null : await PickZip();
        if (path is not null)
        {
            await InstallZipAsync(path);
        }
    }

    /// <summary>Installs the mods found in a zip file (from the picker or dropped onto the window).</summary>
    public async Task InstallZipAsync(string path)
    {
        if (!await EnsureGameClosedAsync())
        {
            return;
        }

        List<ModManagerService.LocalMod> found;
        try
        {
            found = ModManagerService.InspectZip(path);
        }
        catch (PackageException ex)
        {
            await Dialog.ShowAsync("This zip cannot be used", ex.Message, "OK");
            return;
        }

        if (found.Count == 0)
        {
            List<string> plugins = ModManagerService.FindPluginFiles(path);
            if (plugins.Count > 0)
            {
                // a Sacred-Bild or Sacred Gear package: not a mod, but the manager installs it from the source (and then finds its updates)
                DialogResult pluginAnswer = await Dialog.ShowAsync("This is a plugin, not a mod",
                    string.Join(" and ", plugins) + " belongs to Sacred-Bild / Sacred Gear. Those are plugins, and the manager installs them from a source, which also finds their updates. "
                    + "Add the Sacred-Bild source, then install Sacred-Bild and Sacred Gear from Browse (tab Plugins).", "Add the Sacred-Bild source", "Cancel");
                if (pluginAnswer.Primary)
                {
                    await AddSourceDirectlyAsync(AppSettings.SacredBildSource);
                    SectionIndex = 1;
                    Browse.CategoryIndex = 2;
                }

                return;
            }

            await Dialog.ShowAsync("No mod in this zip", "A Sacred Gear mod is a folder with a mod.lua file. This zip has none.", "OK");
            return;
        }

        DialogResult answer = await Dialog.ShowAsync("Install from " + Path.GetFileName(path) + "?", found.Count == 1 ? "This mod will be installed." : found.Count + " mods will be installed.", "Install", "Cancel",
            lines: found.Select(f => f.Manifest.DisplayName + (f.Manifest.Version.Length > 0 ? "  " + f.Manifest.Version : string.Empty)));
        if (!answer.Primary)
        {
            return;
        }

        var errors = new List<string>();
        int done = 0;
        await RunExclusiveAsync(() =>
        {
            foreach (ModManagerService.LocalMod f in found)
            {
                try
                {
                    _service.InstallLocal(_store!, path, f.PathInZip);
                    done++;
                }
                catch (Exception ex) when (ex is StoreException or PackageException)
                {
                    errors.Add(f.Manifest.Id + ": " + ex.Message);
                }
            }

            return Task.CompletedTask;
        });
        await ReloadInstalledAsync();
        if (errors.Count > 0)
        {
            await Dialog.ShowAsync("Some mods could not be installed", string.Empty, "OK", lines: errors);
        }

        SetStatus(done == 0 ? "Nothing was installed." : done == 1 ? "Installed 1 mod from the zip." : $"Installed {done} mods from the zip.", error: done == 0);
    }

    // ---- sources and settings -----------------------------------------------------------------------------------

    public void SaveSettings()
    {
        if (_settingsPath is not null)
        {
            SettingsFile.Save(_settingsPath, Settings);
        }
    }

    public void ApplyTheme()
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = Settings.Theme switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    public void Shutdown()
    {
        _poll.Stop();
        _catalogCts.Cancel();
        SaveSettings();
    }

    public void Dispose()
    {
        _catalogCts.Dispose();
        _thumbnailGate.Dispose();
        _fetcher.Dispose();
    }

    /// <summary>Called when the window gets the focus again: what the game or the player changed outside is picked up.</summary>
    public void OnWindowActivated()
    {
        if (HasGame && !_busy)
        {
            PollGame();
            _ = ReloadInstalledAsync();
        }
    }
}
