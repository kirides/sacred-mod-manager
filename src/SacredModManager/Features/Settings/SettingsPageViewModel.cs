using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SacredModManager.Core.App;
using SacredModManager.Core.Links;
using SacredModManager.Features.Shell;

namespace SacredModManager.Features.Settings;

/// <summary>The Settings page: the game folder, the look, safety questions, and where the manager keeps its files.</summary>
public sealed partial class SettingsPageViewModel(MainWindowViewModel app) : ObservableObject
{
    private bool _loading;

    public IReadOnlyList<string> Themes { get; } = ["Follow the system", "Light", "Dark"];

    [ObservableProperty]
    public partial string ToolsFolderText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FolderText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FolderMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool FolderOk { get; set; }

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    [ObservableProperty]
    public partial bool AskBeforeRemove { get; set; } = true;

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; } = true;

    [ObservableProperty]
    public partial bool RegisterLinks { get; set; } = true;

    [ObservableProperty]
    public partial string LinkStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool LinkOk { get; set; }

    public bool LinksSupported => UriSchemeRegistrar.IsSupported;
    public bool CanTakeOverLinks => LinksSupported && RegisterLinks && !LinkOk;

    public string DataFolder => app.Paths.Data;
    public string SettingsFile => app.Paths.Settings;
    public bool HasFolderMessage => FolderMessage.Length > 0;
    /// <summary>"Gear Mod Manager nightly-20261120-abc1234": which build this is (so that an old one is easy to recognise).</summary>
    public string Version { get; } = "Gear Mod Manager " + (AppBuild.Stamp == "dev" ? "(development build)" : AppBuild.Stamp);

    partial void OnFolderMessageChanged(string value) => OnPropertyChanged(nameof(HasFolderMessage));

    public void Reload()
    {
        _loading = true;
        FolderText = app.Settings.GameFolder.Length > 0 ? app.Settings.GameFolder : app.GameFolder;
        ToolsFolderText = app.ToolsRoot;
        ThemeIndex = app.Settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        AskBeforeRemove = app.Settings.AskBeforeRemove;
        CheckForUpdates = app.Settings.CheckForUpdates;
        RegisterLinks = app.Settings.RegisterLinks;
        RefreshLinkState();
        FolderOk = app.HasGame;
        FolderMessage = app.HasGame ? "Found Sacred.exe. Mods go into " + Path.Combine(app.GameFolder, "mods") : "No Sacred.exe in this folder.";
        _loading = false;
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (_loading)
        {
            return;
        }

        app.Settings.Theme = value switch { 1 => "light", 2 => "dark", _ => "system" };
        app.ApplyTheme();
        app.SaveSettings();
    }

    partial void OnLinkOkChanged(bool value) => OnPropertyChanged(nameof(CanTakeOverLinks));

    /// <summary>Reads the registration and writes it as text.</summary>
    public void RefreshLinkState()
    {
        RegistrationState state = app.LinkRegistration();
        LinkOk = state == RegistrationState.Registered;
        LinkStatus = state switch
        {
            RegistrationState.Registered => "gear-mod-manager:// links open in this program.",
            RegistrationState.RegisteredElsewhere => "Links open in another copy of the manager (or another program).",
            RegistrationState.NotRegistered => RegisterLinks ? "Links are not registered yet." : "Links are off.",
            _ => "Links are only supported on Windows and Linux.",
        };
        OnPropertyChanged(nameof(CanTakeOverLinks));
    }

    partial void OnRegisterLinksChanged(bool value)
    {
        OnPropertyChanged(nameof(CanTakeOverLinks));
        if (_loading)
        {
            return;
        }

        app.Settings.RegisterLinks = value;
        app.SaveSettings();
        string? error = app.SetLinkRegistration(value);
        RefreshLinkState();
        if (error is not null)
        {
            LinkStatus = error;
            LinkOk = false;
        }
    }

    [RelayCommand]
    private void UseThisCopyForLinks()
    {
        string? error = app.SetLinkRegistration(true);
        RefreshLinkState();
        if (error is not null)
        {
            LinkStatus = error;
        }
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        app.Settings.CheckForUpdates = value;
        app.SaveSettings();
        if (value)
        {
            _ = app.CheckForAppUpdateAsync();
        }
        else
        {
            app.ClearAppUpdate();
        }
    }

    partial void OnAskBeforeRemoveChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        app.Settings.AskBeforeRemove = value;
        app.SaveSettings();
    }

    [RelayCommand]
    private async Task Browse()
    {
        string? picked = app.PickFolder is null ? null : await app.PickFolder("Select the Sacred Gold folder (with Sacred.exe)", FolderText);
        if (picked is not null)
        {
            FolderText = picked;
            await Apply();
        }
    }

    [RelayCommand]
    private async Task Apply()
    {
        string? error = await app.SetGameFolderAsync(FolderText.Trim().Trim('"'));
        if (error is not null)
        {
            FolderOk = false;
            FolderMessage = error;
        }
    }

    [RelayCommand]
    private async Task BrowseTools()
    {
        string? picked = app.PickFolder is null ? null : await app.PickFolder("Select the folder tools are unpacked into", ToolsFolderText);
        if (picked is not null)
        {
            ToolsFolderText = picked;
            ApplyTools();
        }
    }

    [RelayCommand]
    private void ApplyTools()
    {
        app.SetToolsFolder(ToolsFolderText);
        ToolsFolderText = app.ToolsRoot;
    }

    [RelayCommand]
    private void ResetTools()
    {
        app.SetToolsFolder(string.Empty);
        ToolsFolderText = app.ToolsRoot;
    }

    [RelayCommand]
    private async Task Detect()
    {
        string? found = GameLocator.Find(null);
        if (found is null)
        {
            FolderOk = false;
            FolderMessage = "Sacred Gold was not found in the usual places. Use Browse.";
            return;
        }

        FolderText = found;
        await Apply();
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        Directory.CreateDirectory(app.Paths.Data);
        app.OpenPath(app.Paths.Data);
    }

    [RelayCommand]
    private void ClearCache()
    {
        try
        {
            if (Directory.Exists(app.Paths.Packages))
            {
                Directory.Delete(app.Paths.Packages, recursive: true);
            }

            if (Directory.Exists(app.Paths.Thumbnails))
            {
                Directory.Delete(app.Paths.Thumbnails, recursive: true);
            }

            app.SetStatus("Downloaded packages and pictures were deleted. Backups are kept.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            app.SetStatus("The cache could not be cleared: " + ex.Message, error: true);
        }
    }
}
