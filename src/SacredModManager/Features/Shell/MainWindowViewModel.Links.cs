using SacredModManager.Core.App;
using SacredModManager.Core.Index;
using SacredModManager.Core.Links;
using SacredModManager.Core.Mods;
using SacredModManager.Features.Dialogs;
using SacredModManager.Features.ModList;

namespace SacredModManager.Features.Shell;

/// <summary>Links (<c>gear-mod-manager://</c>): what the manager does with one, and the registration with the system.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Set by the window: puts text on the clipboard.</summary>
    public Func<string, Task>? SetClipboard { get; set; }

    /// <summary>
    /// A link arrived (from the command line or from a second start). It is checked, and nothing happens without the player's yes:
    /// adding a source and installing a mod both ask first and say where the thing comes from.
    /// </summary>
    public async Task HandleLinkAsync(string text)
    {
        if (!ManagerLink.TryParse(text, out ManagerLink? link, out string? error))
        {
            await Dialog.ShowAsync("This link cannot be used", error ?? "The link is not valid.", "OK");
            return;
        }

        switch (link)
        {
            case ManagerLink.AddIndex add:
                if (await AddSourceFromLinkAsync(add.Url, null))
                {
                    SectionIndex = 1;
                }

                break;
            case ManagerLink.Install install:
                await InstallFromLinkAsync(install);
                break;
        }
    }

    /// <summary>A link or a local archive file arrived (command line, "open with", a second start): both go through the same checks as the equivalent menu actions.</summary>
    public async Task HandleLaunchTargetAsync(string text)
    {
        if (LaunchTarget.Classify(text) == LaunchKind.ArchiveFile)
        {
            await InstallArchiveAsync(text.Trim('"'));
            return;
        }

        await HandleLinkAsync(text);
    }

    /// <summary>Adds a source the player just agreed to (no further question) and reloads.</summary>
    private async Task AddSourceDirectlyAsync(string address)
    {
        if (!Settings.Sources.Any(s => string.Equals(s, address, StringComparison.OrdinalIgnoreCase)))
        {
            Settings.Sources.Add(address);
            SaveSettings();
            Sources.Reload();
        }

        await ReloadCatalogAsync();
    }

    private bool HasSource(Uri url) => Settings.Sources.Any(s => string.Equals(s, url.AbsoluteUri, StringComparison.OrdinalIgnoreCase));

    /// <summary>Asks, then adds the source and reloads. Returns true when the source is in the list afterwards.</summary>
    private async Task<bool> AddSourceFromLinkAsync(Uri url, string? forMod)
    {
        if (HasSource(url))
        {
            if (forMod is null)
            {
                SetStatus("This source is already in your list.");
                SectionIndex = 2;
            }

            return true;
        }

        string title = forMod is null ? "Add this mod source?" : "Add a source and install " + forMod + "?";
        string message = (forMod is null ? "A link wants to add this source." : "The mod '" + forMod + "' comes from a source you do not have yet.")
                         + " A source can install mods, and mods change the game. Only continue if you trust it.";
        DialogResult answer = await Dialog.ShowAsync(title, message, forMod is null ? "Add source" : "Add source and continue", "Cancel", lines: [url.AbsoluteUri]);
        if (!answer.Primary)
        {
            return false;
        }

        Settings.Sources.Add(url.AbsoluteUri);
        SaveSettings();
        Sources.Reload();
        await ReloadCatalogAsync();
        return true;
    }

    private async Task InstallFromLinkAsync(ManagerLink.Install install)
    {
        if (install.Index is { } index && !await AddSourceFromLinkAsync(index, install.ModId))
        {
            return;
        }

        if (_catalog is null)
        {
            await ReloadCatalogAsync();
        }

        for (int i = 0; i < 300 && IsLoadingCatalog; i++)
        {
            await Task.Delay(100); // a load is running: wait for it (30 s at most)
        }

        ModItemViewModel? item = AllItems.FirstOrDefault(m => m.Id == install.ModId && m.IsAvailable);
        if (item is null)
        {
            await Dialog.ShowAsync("Mod not found", "The mod '" + install.ModId + "' is not offered by your sources"
                                                    + (install.Index is null ? ". Ask for a link that names the index it comes from." : " (checked " + install.Index.AbsoluteUri + ")."), "OK");
            return;
        }

        SectionIndex = 1;
        Browse.ClearSearch();
        Browse.FilterIndex = 0;
        Browse.Refresh();
        Browse.Selected = item;
        if (item.IsInstalled && !item.UpdateAvailable)
        {
            await Dialog.ShowAsync(item.Name + " is already installed", "Version " + item.Version + " is installed and up to date.", "OK");
            return;
        }

        CatalogMod mod = item.Catalog!;
        var lines = new List<string> { item.Name + (mod.Version.Length > 0 ? "  " + mod.Version : string.Empty) };
        if (mod.Author.Length > 0)
        {
            lines.Add("by " + mod.Author);
        }

        lines.Add("from " + mod.IndexName);
        if (mod.Requires.Count > 0)
        {
            lines.Add("needs " + string.Join(", ", mod.Requires));
        }

        DialogResult answer = await Dialog.ShowAsync((item.UpdateAvailable ? "Update " : "Install ") + item.Name + "?", "A link asks to " + (item.UpdateAvailable ? "update" : "install") + " this mod.",
            item.UpdateAvailable ? "Update" : "Install", "Cancel", lines: lines);
        if (answer.Primary)
        {
            await InstallAsync(item);
        }
    }

    // ---- registration with the system ---------------------------------------------------------------------------

    /// <summary>The program file that would be registered (the AppImage inside an AppImage; null when running through the dotnet host).</summary>
    private static string? OwnExecutable() => AppExecutable.Path();

    public RegistrationState LinkRegistration()
        => OwnExecutable() is { } exe ? UriSchemeRegistrar.Check(exe) : UriSchemeRegistrar.IsSupported ? RegistrationState.NotRegistered : RegistrationState.Unsupported;

    /// <summary>
    /// At start (normal mode only): when the player wants links and nothing is registered, or what is registered points to a program
    /// that no longer exists, register this program. A registration for another existing copy is left alone (Settings can take it over).
    /// </summary>
    private void EnsureLinkRegistration()
    {
        if (_settingsPath is null || !Settings.RegisterLinks || !UriSchemeRegistrar.IsSupported || OwnExecutable() is not { } exe)
        {
            return;
        }

        try
        {
            RegistrationState state = UriSchemeRegistrar.Check(exe);
            if (state == RegistrationState.NotRegistered)
            {
                UriSchemeRegistrar.Register(exe);
            }
        }
        catch (UriSchemeException ex)
        {
            SetStatus("Links could not be registered: " + ex.Message, error: true);
        }
    }

    /// <summary>Registers this program for links (true) or removes the registration (false). Returns an error text or null.</summary>
    public string? SetLinkRegistration(bool on)
    {
        try
        {
            if (on)
            {
                if (OwnExecutable() is not { } exe)
                {
                    return "This copy runs through the dotnet host; start the published SacredModManager program to register it.";
                }

                UriSchemeRegistrar.Register(exe);
            }
            else
            {
                UriSchemeRegistrar.Unregister();
            }

            return null;
        }
        catch (UriSchemeException ex)
        {
            return ex.Message;
        }
    }

    // ---- sharing links ------------------------------------------------------------------------------------------

    public async Task CopyLinkAsync(ModItemViewModel item)
    {
        if (item.Catalog is not { } mod)
        {
            return;
        }

        await CopyTextAsync(ManagerLink.ForInstall(item.Id, mod.IndexUrl.IsFile ? null : mod.IndexUrl.AbsoluteUri), "Install link for " + item.Name + " copied.");
    }

    public async Task CopyTextAsync(string text, string status)
    {
        if (SetClipboard is null)
        {
            return;
        }

        await SetClipboard(text);
        SetStatus(status);
    }
}
