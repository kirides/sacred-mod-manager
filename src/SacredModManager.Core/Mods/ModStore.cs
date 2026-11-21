using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace SacredModManager.Core.Mods;

/// <summary>A mod found in <c>&lt;game&gt;\mods\</c>.</summary>
public sealed class InstalledMod
{
    /// <summary>A Sacred Gear mod, a plugin (files in the game folder) or a tool (a program in the tools folder).</summary>
    public ItemKind Kind { get; init; } = ItemKind.Mod;

    public required string Folder { get; init; }
    public required string Directory { get; init; }

    /// <summary>Null when mod.lua could not be read; see <see cref="Error"/>.</summary>
    public ModManifest? Manifest { get; init; }

    public string? Error { get; init; }

    /// <summary>The manager's record, null for a mod that was put there by hand.</summary>
    public TrackedMod? Tracked { get; init; }

    /// <summary>On/Off as Sacred Gear will see it: the saved choice, else the mod's own default.</summary>
    public bool Enabled { get; init; }

    /// <summary>Files the manager wrote that were changed since (by hand or by the mod itself).</summary>
    public int ModifiedFiles { get; init; }

    public int MissingFiles { get; init; }

    public string Id => Manifest?.Id ?? Tracked?.Id ?? Folder;
    public string DisplayName => Manifest?.DisplayName ?? Tracked?.Id ?? Folder;
    public string Version => Manifest?.Version ?? Tracked?.Version ?? string.Empty;
}

/// <summary>A copy of a mod folder taken before the manager changed or removed it.</summary>
public sealed class BackupInfo
{
    public required string Id { get; init; }
    public required string Directory { get; init; }
    public string Version { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public DateTime When { get; init; }
}

/// <summary>What an install did.</summary>
public sealed class InstallOutcome
{
    public required string Id { get; init; }
    public required string Folder { get; init; }
    public string Version { get; init; } = string.Empty;
    public string? PreviousVersion { get; init; }
    public int FilesWritten { get; init; }
    public int ReplacedModified { get; init; }
    public BackupInfo? Backup { get; init; }
}

/// <summary>An operation that cannot be done; the message says why in words for the player.</summary>
public class StoreException(string message) : Exception(message);

/// <summary>
/// A plugin's target files belong to another installed plugin. Thrown before anything was changed; the caller may ask the player and, on a yes,
/// remove <see cref="ConflictingId"/> and install again.
/// </summary>
public sealed class PluginConflictException(string pluginId, string conflictingId, IReadOnlyList<string> files)
    : StoreException($"'{files[0]}' belongs to the plugin '{conflictingId}'")
{
    public string PluginId { get; } = pluginId;

    public string ConflictingId { get; } = conflictingId;

    /// <summary>The target files (forward slashes, relative to the game folder) that the other plugin owns.</summary>
    public IReadOnlyList<string> Files { get; } = files;
}

/// <summary>
/// The mods folder of one game folder: scanning it, installing from a package, updating, removing and restoring. It is the only
/// place that writes below <c>&lt;game&gt;\mods\&lt;folder&gt;</c>, and it never writes anywhere else in the game folder. Before a mod is
/// replaced or removed its folder is copied to the backup folder (app data), so every change can be undone.
/// </summary>
public sealed partial class ModStore
{
    public const int BackupsKept = 5;

    private readonly string _backupRoot;

    /// <param name="gameFolder">The Sacred Gold folder (with Sacred.exe).</param>
    /// <param name="backupRoot">Where backups and staging folders go.</param>
    /// <param name="toolsRoot">Where tools are unpacked (one folder per tool); default: <c>tools</c> next to the backups.</param>
    public ModStore(string gameFolder, string backupRoot, string? toolsRoot = null)
    {
        GameFolder = Path.GetFullPath(gameFolder);
        ModsDir = Path.Combine(GameFolder, "mods");
        _backupRoot = backupRoot;
        ToolsRoot = Path.GetFullPath(toolsRoot ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(backupRoot)) ?? backupRoot, "tools"));
        State = new GearModState(ModsDir);
        Registry = new InstallRegistry(ModsDir);
    }

    public string GameFolder { get; }
    public string ModsDir { get; }
    public string ToolsRoot { get; }
    public GearModState State { get; }
    public InstallRegistry Registry { get; }

    /// <summary>Reads every mod folder (its mod.lua in the sandbox), the saved On/Off choices and the manager's record.</summary>
    public async Task<List<InstalledMod>> ScanAsync(CancellationToken ct = default)
    {
        var list = new List<InstalledMod>();
        InstallRegistryFile registry = Registry.Load();
        list.AddRange(ScanItems(registry));
        if (!System.IO.Directory.Exists(ModsDir))
        {
            return list;
        }

        Dictionary<string, bool> saved = SafeEnabled();
        var byFolder = registry.Mods.Values.Where(t => t.ItemKind == ItemKind.Mod).GroupBy(t => t.Folder, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (string dir in System.IO.Directory.EnumerateDirectories(ModsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            string folder = Path.GetFileName(dir);
            string manifestPath = Path.Combine(dir, "mod.lua");
            if (!File.Exists(manifestPath))
            {
                if (File.Exists(Path.Combine(dir, "main.lua")))
                {
                    list.Add(new InstalledMod { Folder = folder, Directory = dir, Error = "mod.lua is missing" });
                }

                continue;
            }

            ModManifest? manifest = null;
            string? error = null;
            try
            {
                manifest = await LuaManifestReader.ReadAsync(File.ReadAllText(manifestPath), "mod.lua").ConfigureAwait(false);
            }
            catch (ManifestException ex)
            {
                error = ex.Message;
            }
            catch (IOException ex)
            {
                error = ex.Message;
            }

            byFolder.TryGetValue(folder, out TrackedMod? tracked);
            (int modified, int missing) = tracked is null ? (0, 0) : Compare(dir, tracked);
            list.Add(new InstalledMod
            {
                Folder = folder, Directory = dir, Manifest = manifest, Error = error, Tracked = tracked, ModifiedFiles = modified, MissingFiles = missing,
                Enabled = manifest is null ? false : saved.TryGetValue(manifest.Id, out bool on) ? on : manifest.DefaultEnabled,
            });
        }

        return list;
    }

    private Dictionary<string, bool> SafeEnabled()
    {
        try
        {
            return State.ReadEnabled();
        }
        catch (GearStateException)
        {
            return [];
        }
    }

    private static (int Modified, int Missing) Compare(string dir, TrackedMod tracked)
    {
        int modified = 0, missing = 0;
        foreach ((string rel, string hash) in tracked.Files)
        {
            string path = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                missing++;
            }
            else if (HashFile(path) != hash)
            {
                modified++;
            }
        }

        return (modified, missing);
    }

    private static string HashFile(string path)
    {
        using FileStream s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    /// <summary>
    /// Installs (or updates) the mod found at <paramref name="pathInArchive"/> of the package. The mod.lua in the zip must have the expected id.
    /// An existing folder is backed up first; files the manager wrote earlier that the new version no longer has are removed; files
    /// that are not the manager's (settings the mod saved, config.lua) stay.
    /// </summary>
    public InstallOutcome Install(ArchivePackage package, string pathInArchive, string? expectedId, string? indexUrl, string? packageUrl)
    {
        if (!package.HasFolder(pathInArchive))
        {
            throw new StoreException($"the zip has no mod at '{pathInArchive}' (no mod.lua there)");
        }

        ModManifest manifest = ReadManifest(package, pathInArchive);
        if (expectedId is not null && !string.Equals(manifest.Id, expectedId, StringComparison.Ordinal))
        {
            throw new StoreException($"the zip's mod at '{pathInArchive}' has the id '{manifest.Id}', the index promised '{expectedId}'");
        }

        string folder = pathInArchive.Length == 0 ? manifest.Id : pathInArchive[(pathInArchive.LastIndexOf('/') + 1)..];
        CheckFolderName(folder);
        List<PackageFile> files = package.FilesUnder(pathInArchive);

        System.IO.Directory.CreateDirectory(ModsDir);
        InstallRegistryFile registry = Registry.Load();
        string target = Path.Combine(ModsDir, folder);
        TrackedMod? existing = registry.Mods.GetValueOrDefault(manifest.Id);
        if (existing is not null && existing.ItemKind != ItemKind.Mod)
        {
            throw new StoreException($"'{manifest.Id}' is already installed as a {ItemKinds.Singular(existing.ItemKind).ToLowerInvariant()}");
        }

        string? clash = FolderOfOtherMod(manifest.Id, folder);
        if (clash is not null)
        {
            throw new StoreException($"the mod '{manifest.Id}' is already installed in the folder '{clash}'. Remove it first (or update that copy).");
        }

        existing ??= registry.Mods.Values.FirstOrDefault(t => string.Equals(t.Folder, folder, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !string.Equals(existing.Id, manifest.Id, StringComparison.Ordinal))
        {
            throw new StoreException($"the folder '{folder}' belongs to the mod '{existing.Id}'");
        }

        string staging = Path.Combine(_backupRoot, "staging", Guid.NewGuid().ToString("N"));
        BackupInfo? backup = null;
        string? previous = null;
        try
        {
            Dictionary<string, string> hashes = package.Extract(files, staging);
            if (!File.Exists(Path.Combine(staging, "mod.lua")))
            {
                throw new StoreException("the mod folder has no mod.lua");
            }

            int replacedModified = 0;
            if (System.IO.Directory.Exists(target))
            {
                previous = ReadInstalledVersion(target) ?? existing?.Version;
                backup = CreateBackup(manifest.Id, target, previous ?? string.Empty, existing, existing is null ? "replaced a mod that was installed by hand" : "update");
                if (existing is not null)
                {
                    // an update only overwrites: nothing is deleted, not even a file the new version no longer ships (a dead lua file is better
                    // than a lost file: mods keep their own data in their folder). Such files stay in the record, so that Remove cleans them up.
                    replacedModified = CountDifferent(existing, hashes, target);
                }
            }

            CopyDirectory(staging, target);
            registry.Mods[manifest.Id] = new TrackedMod
            {
                Id = manifest.Id, Folder = folder, Version = manifest.Version, IndexUrl = indexUrl, PackageUrl = packageUrl,
                InstalledAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), Files = KeepRecordedFiles(existing, hashes),
            };
            Registry.Save(registry);
            Prune(manifest.Id);
            return new InstallOutcome
            {
                Id = manifest.Id, Folder = folder, Version = manifest.Version, PreviousVersion = previous, FilesWritten = hashes.Count,
                ReplacedModified = replacedModified, Backup = backup,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (backup is not null)
            {
                TryRestoreFolder(backup, target);
            }

            throw new StoreException("writing the mod failed (" + ex.Message + "). Is the game running?");
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Removes a mod. The files the manager wrote go; what else the folder holds (data the mod saved, config.lua) stays unless
    /// <paramref name="removeData"/>. A mod the manager did not install is removed completely. The folder is backed up first.
    /// </summary>
    public BackupInfo? Uninstall(InstalledMod mod, bool removeData, bool forgetSettings)
    {
        if (mod.Kind != ItemKind.Mod)
        {
            UninstallItem(mod, removeData);
            return null;
        }

        if (!System.IO.Directory.Exists(mod.Directory) || !IsInsideMods(mod.Directory))
        {
            throw new StoreException("the mod folder does not exist any more");
        }

        InstallRegistryFile registry = Registry.Load();
        TrackedMod? tracked = mod.Tracked ?? registry.Mods.GetValueOrDefault(mod.Id);
        BackupInfo? backup;
        try
        {
            backup = CreateBackup(mod.Id, mod.Directory, mod.Version, tracked, "removed");
            if (tracked is null || removeData)
            {
                System.IO.Directory.Delete(mod.Directory, recursive: true);
            }
            else
            {
                foreach (string rel in tracked.Files.Keys)
                {
                    TryDelete(Path.Combine(mod.Directory, rel.Replace('/', Path.DirectorySeparatorChar)));
                }

                RemoveEmptyDirectories(mod.Directory);
                if (!System.IO.Directory.EnumerateFileSystemEntries(mod.Directory).Any())
                {
                    System.IO.Directory.Delete(mod.Directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StoreException("removing the mod failed (" + ex.Message + "). Is the game running?");
        }

        registry.Mods.Remove(mod.Id);
        if (System.IO.Directory.Exists(ModsDir))
        {
            Registry.Save(registry);
        }

        if (forgetSettings && mod.Manifest is not null)
        {
            State.Forget(mod.Manifest.Id);
        }

        return backup;
    }

    // ---- backups ------------------------------------------------------------------------------------------------

    public List<BackupInfo> Backups(string id)
    {
        var list = new List<BackupInfo>();
        string dir = Path.Combine(_backupRoot, "mods", id);
        if (!System.IO.Directory.Exists(dir))
        {
            return list;
        }

        foreach (string d in System.IO.Directory.EnumerateDirectories(dir))
        {
            string meta = Path.Combine(d, "backup.json");
            try
            {
                BackupMeta? m = JsonSerializer.Deserialize(File.ReadAllBytes(meta), BackupJsonContext.Default.BackupMeta);
                if (m is not null && System.IO.Directory.Exists(Path.Combine(d, "files")))
                {
                    list.Add(new BackupInfo { Id = id, Directory = d, Version = m.Version, Reason = m.Reason, When = DateTime.Parse(m.When, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) });
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or FormatException)
            {
                // a backup folder without a readable record is ignored
            }
        }

        return [.. list.OrderByDescending(b => b.When)];
    }

    /// <summary>Puts a backup back (the mod as it is now is backed up first, so a restore can be undone too).</summary>
    public void Restore(BackupInfo backup)
    {
        BackupMeta meta;
        try
        {
            meta = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(backup.Directory, "backup.json")), BackupJsonContext.Default.BackupMeta)
                   ?? throw new StoreException("the backup is damaged");
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            throw new StoreException("the backup is damaged or gone");
        }
        string folder = meta.Folder;
        CheckFolderName(folder);
        string target = Path.Combine(ModsDir, folder);
        InstallRegistryFile registry = Registry.Load();
        try
        {
            if (System.IO.Directory.Exists(target))
            {
                CreateBackup(backup.Id, target, ReadInstalledVersion(target) ?? string.Empty, registry.Mods.GetValueOrDefault(backup.Id), "before restoring " + backup.Version);
                System.IO.Directory.Delete(target, recursive: true);
            }

            CopyDirectory(Path.Combine(backup.Directory, "files"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StoreException("restoring failed (" + ex.Message + "). Is the game running?");
        }

        if (meta.Tracked is not null)
        {
            registry.Mods[backup.Id] = meta.Tracked;
        }
        else
        {
            registry.Mods.Remove(backup.Id);
        }

        Registry.Save(registry);
        Prune(backup.Id);
    }

    private BackupInfo? CreateBackup(string id, string sourceDir, string version, TrackedMod? tracked, string reason)
    {
        if (!System.IO.Directory.Exists(sourceDir) || !System.IO.Directory.EnumerateFileSystemEntries(sourceDir).Any())
        {
            return null;
        }

        DateTime now = DateTime.Now;
        string dir = Path.Combine(_backupRoot, "mods", id, now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        CopyDirectory(sourceDir, Path.Combine(dir, "files"));
        var meta = new BackupMeta
        {
            Folder = Path.GetFileName(sourceDir), Version = version, Reason = reason, When = now.ToString("o", CultureInfo.InvariantCulture), Tracked = tracked,
        };
        File.WriteAllBytes(Path.Combine(dir, "backup.json"), JsonSerializer.SerializeToUtf8Bytes(meta, BackupJsonContext.Default.BackupMeta));
        return new BackupInfo { Id = id, Directory = dir, Version = version, Reason = reason, When = now };
    }

    private void Prune(string id)
    {
        foreach (BackupInfo old in Backups(id).Skip(BackupsKept))
        {
            TryDeleteDirectory(old.Directory);
        }
    }

    private static void TryRestoreFolder(BackupInfo backup, string target)
    {
        try
        {
            TryDeleteDirectory(target);
            CopyDirectory(Path.Combine(backup.Directory, "files"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: the backup is still there
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static ModManifest ReadManifest(ArchivePackage package, string pathInArchive)
    {
        string? text = package.ReadText(pathInArchive.Length == 0 ? "mod.lua" : pathInArchive + "/mod.lua", LuaManifestReader.MaxSourceChars) ?? throw new StoreException("mod.lua cannot be read from the zip");
        try
        {
            return LuaManifestReader.Read(text);
        }
        catch (ManifestException ex)
        {
            throw new StoreException(ex.Message);
        }
    }

    private string? ReadInstalledVersion(string dir)
    {
        try
        {
            return LuaManifestReader.Read(File.ReadAllText(Path.Combine(dir, "mod.lua"))).Version;
        }
        catch (Exception ex) when (ex is ManifestException or IOException)
        {
            return null;
        }
    }

    /// <summary>The folder another copy of the same mod id lives in (not the one we are about to write), or null.</summary>
    private string? FolderOfOtherMod(string id, string folder)
    {
        if (!System.IO.Directory.Exists(ModsDir))
        {
            return null;
        }

        foreach (string dir in System.IO.Directory.EnumerateDirectories(ModsDir))
        {
            string name = Path.GetFileName(dir);
            if (string.Equals(name, folder, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(dir, "mod.lua")))
            {
                continue;
            }

            try
            {
                if (LuaManifestReader.Read(File.ReadAllText(Path.Combine(dir, "mod.lua"))).Id == id)
                {
                    return name;
                }
            }
            catch (Exception ex) when (ex is ManifestException or IOException)
            {
                // an unreadable mod.lua is not a clash
            }
        }

        return null;
    }

    /// <summary>The record of an update: the files of the new version, plus the files of the old version it no longer ships (they are still ours to remove later).</summary>
    internal static Dictionary<string, string> KeepRecordedFiles(TrackedMod? existing, Dictionary<string, string> fresh)
    {
        var all = new Dictionary<string, string>(fresh, StringComparer.OrdinalIgnoreCase);
        foreach ((string rel, string hash) in existing?.Files ?? [])
        {
            all.TryAdd(rel, hash);
        }

        return all;
    }

    private static int CountDifferent(TrackedMod existing, Dictionary<string, string> incoming, string target)
    {
        int n = 0;
        foreach ((string rel, string recorded) in existing.Files)
        {
            string path = Path.Combine(target, rel.Replace('/', Path.DirectorySeparatorChar));
            if (incoming.ContainsKey(rel) && File.Exists(path) && HashFile(path) != recorded)
            {
                n++;
            }
        }

        return n;
    }

    private bool IsInsideMods(string dir)
        => Path.GetFullPath(dir).StartsWith(ModsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>The characters no folder name may hold, on any platform (Windows' rules: a mod must install on both).</summary>
    private static readonly System.Buffers.SearchValues<char> InvalidFolderChars = System.Buffers.SearchValues.Create("<>:\"/\\|?*");

    internal static void CheckFolderName(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.AsSpan().IndexOfAny(InvalidFolderChars) >= 0 || folder.Any(char.IsControl) || folder is "." or ".."
            || folder.StartsWith('.') || folder.EndsWith(' ') || folder.EndsWith('.'))
        {
            throw new StoreException($"'{folder}' cannot be used as a folder name");
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        System.IO.Directory.CreateDirectory(to);
        foreach (string dir in System.IO.Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            System.IO.Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }

        foreach (string file in System.IO.Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        foreach (string dir in System.IO.Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!System.IO.Directory.EnumerateFileSystemEntries(dir).Any())
            {
                TryDeleteDirectory(dir);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left in place: the next install overwrites it, a removal reports an incomplete folder
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path))
            {
                System.IO.Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
