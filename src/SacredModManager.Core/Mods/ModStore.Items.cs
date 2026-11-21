using System.Globalization;
using System.Security.Cryptography;

namespace SacredModManager.Core.Mods;

/// <summary>Plugins (files for the game folder) and tools (programs in the tools folder): installing, listing, removing.</summary>
public sealed partial class ModStore
{
    /// <summary>The folder a tool is unpacked into.</summary>
    public string ToolDirectory(string id) => Path.Combine(ToolsRoot, id);

    private string OriginalsDirectory(string id) => Path.Combine(_backupRoot, "items", id, "originals");

    /// <summary>
    /// Installs a plugin: copies files of the package into the game folder (<paramref name="files"/> maps a path in the package to a path below
    /// <paramref name="installTo"/>; empty means every file under <paramref name="pathInArchive"/> keeping its relative path). A file the player already
    /// has at a target is copied to the backup folder first and put back when the plugin is removed. Never overwrites a program (.exe), never
    /// touches a file another item installed, never leaves the game folder.
    /// </summary>
    public InstallOutcome InstallPlugin(ArchivePackage package, string id, string version, string pathInArchive, string installTo,
        IReadOnlyList<(string From, string To)> files, string? indexUrl, string? packageUrl)
    {
        CheckItemId(id);
        List<PackageFile> inPackage = package.FilesUnder(pathInArchive);
        var plan = new List<(PackageFile File, string Dest)>();
        if (files.Count == 0)
        {
            plan.AddRange(inPackage.Select(f => (f, Join(installTo, f.RelativePath))));
        }
        else
        {
            foreach ((string from, string to) in files)
            {
                List<(PackageFile, string)> matches;
                if (PackageGlob.IsPattern(from))
                {
                    // a wildcard: 'to' is the target folder, the part of the path below the pattern's literal base is kept
                    if (!PackageGlob.IsValid(from) || PackageGlob.IsPattern(to))
                    {
                        throw new StoreException($"'{from}' is not a valid file pattern");
                    }

                    string baseFolder = PackageGlob.BaseFolder(from);
                    matches = inPackage.Where(f => PackageGlob.IsMatch(from, f.RelativePath))
                        .Select(f => (f, Join(installTo, Join(to, baseFolder.Length == 0 ? f.RelativePath : f.RelativePath[(baseFolder.Length + 1)..])))).ToList();
                }
                else
                {
                    matches = inPackage.Where(f => string.Equals(f.RelativePath, from, StringComparison.OrdinalIgnoreCase))
                        .Select(f => (f, Join(installTo, to))).ToList();
                    if (matches.Count == 0)
                    {
                        matches = inPackage.Where(f => f.RelativePath.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase))
                            .Select(f => (f, Join(installTo, to + f.RelativePath[from.Length..]))).ToList();
                    }
                }

                if (matches.Count == 0)
                {
                    throw new StoreException($"the package has no '{from}'");
                }

                plan.AddRange(matches);
            }
        }

        if (plan.Count == 0)
        {
            throw new StoreException("the package has no files for this plugin");
        }

        InstallRegistryFile registry = Registry.Load();
        TrackedMod? existing = registry.Mods.GetValueOrDefault(id);
        if (existing is not null && existing.ItemKind != ItemKind.Plugin)
        {
            throw new StoreException($"'{id}' is already installed as a {ItemKinds.Singular(existing.ItemKind).ToLowerInvariant()}");
        }

        var seenDest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((PackageFile _, string dest) in plan)
        {
            ArchivePackage.CheckName(dest, dest);
            if (!seenDest.Add(dest))
            {
                throw new StoreException($"two files of the plugin go to '{dest}'");
            }

            CheckPluginTarget(registry, id, existing, dest, plan.Select(p => p.Dest).ToList());
        }

        string staging = Path.Combine(_backupRoot, "staging", Guid.NewGuid().ToString("N"));
        var replaced = new List<string>(existing?.Replaced ?? []);
        try
        {
            Dictionary<string, string> hashes = package.Extract(plan.Select(p => new PackageFile(p.File.EntryName, p.Dest, p.File.Size)), staging);

            // what the player had at the targets is saved once, before the first plugin file lands there
            foreach (string dest in hashes.Keys)
            {
                string target = GamePath(dest);
                if (File.Exists(target) && !(existing?.Files.ContainsKey(dest) ?? false) && !replaced.Contains(dest, StringComparer.OrdinalIgnoreCase))
                {
                    string saved = Path.Combine(OriginalsDirectory(id), dest.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    File.Copy(target, saved, overwrite: false);
                    replaced.Add(dest);
                }
            }

            // an update only overwrites; files the new version no longer ships stay (and stay in the record, so that Remove takes them away)
            CopyDirectory(staging, GameFolder);
            Directory.CreateDirectory(ModsDir);
            registry.Mods[id] = new TrackedMod
            {
                Id = id, Kind = ItemKinds.ToJson(ItemKind.Plugin), Folder = id, Version = version, IndexUrl = indexUrl, PackageUrl = packageUrl,
                InstalledAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), Files = KeepRecordedFiles(existing, hashes), Replaced = replaced.Count == 0 ? null : replaced,
            };
            Registry.Save(registry);
            return new InstallOutcome { Id = id, Folder = id, Version = version, PreviousVersion = existing?.Version, FilesWritten = hashes.Count };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StoreException("writing the files failed (" + ex.Message + "). Is the game running?");
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>Installs a tool: unpacks everything under <paramref name="pathInArchive"/> into <c>&lt;tools&gt;\&lt;id&gt;</c>.</summary>
    public InstallOutcome InstallTool(ArchivePackage package, string id, string version, string pathInArchive, string? launch, string? indexUrl, string? packageUrl)
    {
        CheckItemId(id);
        List<PackageFile> files = package.FilesUnder(pathInArchive);
        if (files.Count == 0)
        {
            throw new StoreException("the package has no files for this tool");
        }

        if (launch is not null && !files.Any(f => string.Equals(f.RelativePath, launch, StringComparison.OrdinalIgnoreCase)))
        {
            throw new StoreException($"the package has no '{launch}' to start");
        }

        InstallRegistryFile registry = Registry.Load();
        TrackedMod? existing = registry.Mods.GetValueOrDefault(id);
        if (existing is not null && existing.ItemKind != ItemKind.Tool)
        {
            throw new StoreException($"'{id}' is already installed as a {ItemKinds.Singular(existing.ItemKind).ToLowerInvariant()}");
        }

        string dir = ToolDirectory(id);
        string staging = Path.Combine(_backupRoot, "staging", Guid.NewGuid().ToString("N"));
        try
        {
            Dictionary<string, string> hashes = package.Extract(files, staging);
            // an update only overwrites: nothing is deleted (a tool keeps its own data in its folder)
            CopyDirectory(staging, dir);
            if (launch is not null && !OperatingSystem.IsWindows())
            {
                // the index may spell the file differently from the zip: the file on disk has the zip's spelling
                string actual = files.First(f => string.Equals(f.RelativePath, launch, StringComparison.OrdinalIgnoreCase)).RelativePath;
                string exe = Path.Combine(dir, actual.Replace('/', Path.DirectorySeparatorChar));
                File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }

            Directory.CreateDirectory(ModsDir);
            registry.Mods[id] = new TrackedMod
            {
                Id = id, Kind = ItemKinds.ToJson(ItemKind.Tool), Folder = id, Version = version, Launch = launch, IndexUrl = indexUrl, PackageUrl = packageUrl,
                InstalledAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), Files = KeepRecordedFiles(existing, hashes),
            };
            Registry.Save(registry);
            return new InstallOutcome { Id = id, Folder = id, Version = version, PreviousVersion = existing?.Version, FilesWritten = hashes.Count };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StoreException("writing the tool failed (" + ex.Message + "). Is it running?");
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// The program of a tool as it is on disk: <paramref name="launch"/> may differ in case from the file (the index's spelling), which matters on a
    /// case-sensitive file system. Null when there is no such file.
    /// </summary>
    public string? ResolveToolLaunch(string id, string launch)
    {
        string current = ToolDirectory(id);
        foreach (string part in launch.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, part);
            if (!File.Exists(next) && !Directory.Exists(next))
            {
                string? found = Directory.Exists(current)
                    ? Directory.EnumerateFileSystemEntries(current).FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (found is null)
                {
                    return null;
                }

                next = found;
            }

            current = next;
        }

        return File.Exists(current) ? current : null;
    }

    /// <summary>The installed plugins and tools, from the manager's record: where they are and how many of their files changed or are gone.</summary>
    private List<InstalledMod> ScanItems(InstallRegistryFile registry)
    {
        var list = new List<InstalledMod>();
        foreach (TrackedMod t in registry.Mods.Values.Where(t => t.ItemKind != ItemKind.Mod).OrderBy(t => t.Id, StringComparer.OrdinalIgnoreCase))
        {
            string baseDir = t.ItemKind == ItemKind.Plugin ? GameFolder : ToolDirectory(t.Id);
            (int modified, int missing) = Compare(baseDir, t);
            list.Add(new InstalledMod
            {
                Kind = t.ItemKind, Folder = t.Id, Directory = baseDir, Tracked = t, Enabled = true, ModifiedFiles = modified, MissingFiles = missing,
                Error = missing > 0 && missing == t.Files.Count ? "its files are gone" : null,
            });
        }

        return list;
    }

    /// <summary>Removes a plugin (its files go, files it replaced are put back) or a tool (its files go; its folder too with <paramref name="removeData"/>).</summary>
    private void UninstallItem(InstalledMod item, bool removeData)
    {
        InstallRegistryFile registry = Registry.Load();
        TrackedMod tracked = item.Tracked ?? registry.Mods.GetValueOrDefault(item.Id) ?? throw new StoreException("the manager has no record of this item");
        try
        {
            if (tracked.ItemKind == ItemKind.Plugin)
            {
                var replaced = new List<string>(tracked.Replaced ?? []);
                foreach (string rel in tracked.Files.Keys.ToList())
                {
                    RemovePluginFile(tracked.Id, rel, replaced);
                }

                TryDeleteDirectory(Path.GetDirectoryName(OriginalsDirectory(tracked.Id))!);
            }
            else
            {
                string dir = ToolDirectory(tracked.Id);
                if (removeData)
                {
                    TryDeleteDirectory(dir);
                }
                else if (Directory.Exists(dir))
                {
                    foreach (string rel in tracked.Files.Keys)
                    {
                        TryDelete(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)));
                    }

                    RemoveEmptyDirectories(dir);
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StoreException("removing it failed (" + ex.Message + "). Is the game or the tool running?");
        }

        registry.Mods.Remove(tracked.Id);
        Directory.CreateDirectory(ModsDir);
        Registry.Save(registry);
    }

    /// <summary>Deletes a plugin file; when the plugin had replaced a file there, the original comes back.</summary>
    private void RemovePluginFile(string id, string rel, List<string> replaced)
    {
        string target = GamePath(rel);
        TryDelete(target);
        int index = replaced.FindIndex(r => string.Equals(r, rel, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            string saved = Path.Combine(OriginalsDirectory(id), rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(saved))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(saved, target, overwrite: true);
            }

            replaced.RemoveAt(index);
        }

        // empty folders the plugin created go too, but never the game folder itself
        string? parent = Path.GetDirectoryName(target);
        while (parent is not null && parent.Length > GameFolder.Length && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            TryDeleteDirectory(parent);
            parent = Path.GetDirectoryName(parent);
        }
    }

    private void CheckPluginTarget(InstallRegistryFile registry, string id, TrackedMod? existing, string dest, List<string> allDests)
    {
        string name = Path.GetFileName(dest);
        if (dest.Equals("mods/" + GearModState.FileName, StringComparison.OrdinalIgnoreCase) || dest.Equals("mods/" + InstallRegistry.FileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new StoreException($"a plugin cannot replace '{dest}'");
        }

        string target = GamePath(dest);
        if (File.Exists(target) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !(existing?.Files.ContainsKey(dest) ?? false))
        {
            throw new StoreException($"a plugin cannot replace the program '{dest}'");
        }

        foreach (TrackedMod other in registry.Mods.Values)
        {
            if (!string.Equals(other.Id, id, StringComparison.Ordinal) && other.ItemKind == ItemKind.Plugin && other.Files.Keys.Any(k => string.Equals(k, dest, StringComparison.OrdinalIgnoreCase)))
            {
                string[] owned = allDests.Where(d => other.Files.Keys.Any(k => string.Equals(k, d, StringComparison.OrdinalIgnoreCase))).ToArray();
                throw new PluginConflictException(id, other.Id, owned);
            }
        }
    }

    private string GamePath(string rel)
    {
        string path = Path.GetFullPath(Path.Combine(GameFolder, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(GameFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new StoreException($"'{rel}' is outside the game folder");
        }

        return path;
    }

    private static string Join(string folder, string rel) => folder.Length == 0 ? rel : folder.TrimEnd('/') + "/" + rel;

    private static void CheckItemId(string id)
    {
        if (!ModIdRules.IsValid(id))
        {
            throw new StoreException($"'{id}' is not a valid id");
        }
    }
}
