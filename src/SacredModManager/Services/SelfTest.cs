using SacredModManager.Core.App;
using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;

namespace SacredModManager.Services;

/// <summary>
/// <c>--selftest &lt;index address&gt;</c>: without a window, in a temporary game folder, loads the index, installs a mod with what it
/// requires, switches it off and on, removes it, restores it and reads every installed mod.lua. It proves that the published
/// (Native AOT, trimmed) exe can do the network-free and file work of the manager. Prints one line per check; exit code 0 when all pass.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(string source)
    {
        string root = Path.Combine(Path.GetTempPath(), "sacred-mm-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        int failures = 0;

        void Check(string what, bool ok, string? detail = null)
        {
            Console.Out.WriteLine((ok ? "ok      " : "FAILED  ") + what + (detail is null ? string.Empty : "  (" + detail + ")"));
            if (!ok)
            {
                failures++;
            }
        }

        try
        {
            string game = Path.Combine(root, "game");
            Directory.CreateDirectory(game);
            File.WriteAllBytes(Path.Combine(game, "Sacred.exe"), [0x4D, 0x5A]);
            var paths = new AppPaths(Path.Combine(root, "home"));
            using var fetcher = new Fetcher();
            var service = new ModManagerService(paths, fetcher);
            var store = new ModStore(game, paths.Backups);

            IndexCatalog catalog = await service.LoadCatalogAsync([source]);
            Check("index loaded", catalog.Mods.Count > 0, catalog.Mods.Count + " mods from " + catalog.Indexes.Count + " index(es)");
            foreach (string w in catalog.Warnings)
            {
                Console.Out.WriteLine("note    " + w);
            }

            CatalogMod? pick = catalog.Mods.FirstOrDefault(m => m.Requires.Count > 0) ?? catalog.Mods.FirstOrDefault();
            if (pick is null)
            {
                return 1;
            }

            InstallPlan plan = ModManagerService.Plan([pick], catalog.Mods, []);
            List<BatchItem> results = await service.InstallPlanAsync(store, plan, null, null, CancellationToken.None);
            Check("install " + pick.Id + (plan.Install.Count > 1 ? " with " + (plan.Install.Count - 1) + " required mod(s)" : string.Empty),
                results.All(r => r.Error is null), string.Join("; ", results.Where(r => r.Error is not null).Select(r => r.Id + ": " + r.Error)));

            if (results.Any(r => r.Error is not null))
            {
                return 1;
            }

            List<InstalledMod> installed = await store.ScanAsync();
            Check("every installed mod.lua is read in the Lua sandbox", installed.Count == plan.Install.Count && installed.All(m => m.Manifest is not null),
                string.Join("; ", installed.Where(m => m.Error is not null).Select(m => m.Folder + ": " + m.Error)));
            InstalledMod mod = installed.First(m => m.Id == pick.Id);
            Check("installed mod is tracked and unchanged", mod.Tracked is not null && mod.ModifiedFiles == 0 && mod.MissingFiles == 0);

            store.State.SetEnabled(pick.Id, false);
            Check("switch off writes gear_mods.json", !(await store.ScanAsync()).First(m => m.Id == pick.Id).Enabled && File.Exists(store.State.Path));
            store.State.SetEnabled(pick.Id, true);
            Check("switch on", (await store.ScanAsync()).First(m => m.Id == pick.Id).Enabled);

            File.WriteAllText(Path.Combine(mod.Directory, "config.lua"), "return {}");
            BackupInfo? backup = store.Uninstall(mod, removeData: false, forgetSettings: false);
            Check("remove keeps a backup and the mod's own data", backup is not null && File.Exists(Path.Combine(mod.Directory, "config.lua")) && !File.Exists(Path.Combine(mod.Directory, "mod.lua")));
            store.Restore(backup!);
            installed = await store.ScanAsync();
            Check("restore brings the mod back", installed.Any(m => m.Id == pick.Id && m.Manifest is not null && m.Tracked is not null));

            results = await service.InstallPlanAsync(store, ModManagerService.Plan([pick], catalog.Mods, installed.Select(m => m.Id)), null, null, CancellationToken.None);
            Check("installing again is an update with a backup", results.All(r => r.Error is null && r.Outcome?.Backup is not null));
        }
        catch (Exception ex) when (ex is StoreException or PackageException or FetchException or IndexException or IOException)
        {
            Check("no error", false, ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // a temp folder left behind is harmless
            }
        }

        Console.Out.WriteLine(failures == 0 ? "all checks passed" : failures + " check(s) failed");
        return failures == 0 ? 0 : 1;
    }
}
