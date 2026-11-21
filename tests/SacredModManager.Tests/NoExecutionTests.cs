using System.Text;
using SacredModManager.Core.App;
using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

/// <summary>
/// Indexes and packages are declarative: installing, updating and removing only copy files. Nothing from a package is ever started, and no
/// index field can ask for it. These tests pin that down.
/// </summary>
public sealed class NoExecutionTests : IDisposable
{
    private const string Root = "https://example.com/idx/mod-index.json";
    private readonly TempDir _root = new();
    private readonly FakeFetcher _fetcher = new();
    private readonly AppPaths _paths;
    private readonly ModManagerService _service;

    public NoExecutionTests()
    {
        _paths = new AppPaths(Path.Combine(_root.Path, "home"));
        _service = new ModManagerService(_paths, _fetcher);
        Game = _root.Combine("game");
        Directory.CreateDirectory(Game);
        Store = new ModStore(Game, _paths.Backups, _root.Combine("tools"));
    }

    private string Game { get; }
    private ModStore Store { get; }
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _root.Dispose();

    /// <summary>Scripts that would leave a marker file if anything ran them (next to themselves and in the temp folder).</summary>
    private (string Name, string Content)[] Payload(string folder)
    {
        string marker = Path.Combine(_root.Path, "EXECUTED.marker");
        return
        [
            (folder + "/setup.exe", "MZ-not-a-program"), (folder + "/setup.msi", "msi"),
            (folder + "/run.bat", "echo x > \"" + marker + "\""), (folder + "/run.cmd", "echo x > \"" + marker + "\""),
            (folder + "/run.ps1", "Set-Content '" + marker + "' x"), (folder + "/run.sh", "touch '" + marker + "'"),
            (folder + "/run.vbs", "x"), (folder + "/run.js", "x"), (folder + "/link.lnk", "x"),
            (folder + "/ddraw.dll", "DLL"), (folder + "/readme.txt", "hello"),
        ];
    }

    private bool Executed => File.Exists(Path.Combine(_root.Path, "EXECUTED.marker"));

    private void Serve(string indexJson)
    {
        _fetcher.Add(Root, indexJson);
        List<(string, string)> entries = [.. Payload("plug"), .. Payload("tool"), .. Payload("mods/m")];
        entries.Add(("mods/m/mod.lua", Pack.ModLua("m")));
        entries.Add(("tool/Ed.exe", "EXE"));
        _fetcher.Add("https://example.com/idx/pkg.zip", Pack.Zip([.. entries]));
    }

    [Fact]
    public async Task InstallingEveryKindFromAPackageFullOfScriptsOnlyCopiesAndNeverRuns()
    {
        const string index = """
        {"schema":1,"download":{"url":"pkg.zip","run":"setup.exe"},"run":"setup.exe","script":"run.ps1","mods":[
          {"id":"m","path":"mods/m","run":"mods/m/run.bat","exec":"x","command":"x","postInstall":"run.cmd"},
          {"id":"plug","kind":"plugin","path":"plug","install":{"files":["ddraw.dll"],"run":"setup.exe","script":"run.ps1","launch":"setup.exe"},"installer":"setup.msi","hook":"x"},
          {"id":"tool","kind":"tool","path":"tool","install":{"launch":"Ed.exe","run":"run.bat"},"download":{"url":"pkg.zip","exec":"x"}}
        ]}
        """;
        Serve(index);
        IndexCatalog catalog = await new IndexLoader(_fetcher, "win-x64").LoadAsync([new Uri(Root)], Ct);
        Assert.Equal(3, catalog.Mods.Count);

        foreach (CatalogMod mod in catalog.Mods)
        {
            BatchItem r = (await _service.InstallPlanAsync(Store, ModManagerService.Plan([mod], catalog.Mods, []), null, null, Ct)).Single();
            Assert.Null(r.Error);
        }

        Assert.False(Executed);
        // the plugin installed exactly what its mapping says, and nothing else
        Assert.Equal("DLL", File.ReadAllText(Path.Combine(Game, "ddraw.dll")));
        Assert.False(File.Exists(Path.Combine(Game, "setup.exe")));
        Assert.False(File.Exists(Path.Combine(Game, "run.bat")));
        Assert.False(File.Exists(Path.Combine(Game, "run.ps1")));
        // the tool is unpacked (copied), not started; the launch entry is only remembered for the player's Start button
        Assert.True(File.Exists(Path.Combine(Store.ToolDirectory("tool"), "Ed.exe")));
        Assert.False(Executed);

        // removing and updating run nothing either
        foreach (InstalledMod m in await Store.ScanAsync(Ct))
        {
            Store.Uninstall(m, removeData: true, forgetSettings: false);
        }

        Assert.False(Executed);
    }

    [Fact]
    public async Task FieldsThatAskToRunSomethingAreIgnoredAndWarnedAbout()
    {
        const string index = """
        {"schema":1,"download":{"url":"pkg.zip","run":"a"},"script":"x","mods":[
          {"id":"m","path":"mods/m","exec":"x"},
          {"id":"plug","kind":"plugin","path":"plug","install":{"files":["ddraw.dll"],"command":"y","launch":"setup.exe"},"postInstall":"z"}
        ]}
        """;
        Serve(index);
        IndexCatalog c = await new IndexLoader(_fetcher, "win-x64").LoadAsync([new Uri(Root)], Ct);
        string warnings = string.Join("\n", c.Warnings);
        Assert.Equal(2, c.Mods.Count); // still offered: the fields are ignored, not obeyed
        Assert.Contains("'script' is ignored", warnings);
        Assert.Contains("'exec' is ignored", warnings);
        Assert.Contains("'command' is ignored", warnings);
        Assert.Contains("'postInstall' is ignored", warnings);
        Assert.Contains("install.launch is ignored", warnings);
        Assert.DoesNotContain("'name'", warnings);
    }

    [Fact]
    public async Task UnknownHarmlessFieldsDoNotWarn()
    {
        Serve("{\"schema\":1,\"download\":{\"url\":\"pkg.zip\"},\"mods\":[{\"id\":\"m\",\"path\":\"mods/m\",\"license\":\"MIT\",\"future\":1}]}");
        IndexCatalog c = await new IndexLoader(_fetcher, "win-x64").LoadAsync([new Uri(Root)], Ct);
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public async Task APluginNeverOverwritesAnExistingProgram()
    {
        File.WriteAllText(Path.Combine(Game, "setup.exe"), "THE PLAYERS PROGRAM");
        Serve("{}");
        var plugin = new CatalogMod
        {
            Id = "p", Kind = ItemKind.Plugin, Path = "plug", DownloadUrl = new Uri("https://example.com/idx/pkg.zip"), IndexUrl = new Uri(Root),
            InstallFiles = [("setup.exe", "setup.exe")],
        };
        BatchItem r = (await _service.InstallPlanAsync(Store, ModManagerService.Plan([plugin], [plugin], []), null, null, Ct)).Single();
        Assert.NotNull(r.Error);
        Assert.Equal("THE PLAYERS PROGRAM", File.ReadAllText(Path.Combine(Game, "setup.exe")));
        Assert.False(Executed);
    }

    // ---- where the manager starts processes at all --------------------------------------------------------------

    private static string RepoSrc()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SacredModManager.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir is null ? throw new InvalidOperationException("repository root not found") : Path.Combine(dir, "src");
    }

    /// <summary>
    /// The complete list of places that start a process (see docs/ModIndex.md, "Nothing is ever run"). The install, update, removal, archive, index and link
    /// code is not on it. A change to this list must be a conscious one.
    /// </summary>
    [Fact]
    public void OnlyTheListedPlayerInitiatedPlacesStartProcesses()
    {
        var found = new SortedDictionary<string, int>(StringComparer.Ordinal);
        string src = RepoSrc();
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            {
                continue;
            }

            string text = File.ReadAllText(file, Encoding.UTF8);
            int n = text.Split("Process.Start(").Length - 1 + text.Split("new ProcessStartInfo(").Length - 1;
            if (n > 0)
            {
                found[Path.GetRelativePath(src, file).Replace('\\', '/')] = n;
            }
        }

        Assert.Equal(new Dictionary<string, int>
        {
            // "Start Sacred" button
            ["SacredModManager.Core/App/GameLocator.cs"] = 2,
            // the link registration on Linux: update-desktop-database, no package content involved
            ["SacredModManager.Core/Links/UriSchemeRegistrar.cs"] = 2,
            // a tool's Start button (player clicks, after install), open a folder or the update page in the system's file manager / browser
            ["SacredModManager/Features/Shell/MainWindowViewModel.cs"] = 4,
        }, found);
    }

    [Fact]
    public void TheInstallAndArchiveCodeDoesNotReferenceProcessesOrDynamicCodeLoading()
    {
        string core = Path.Combine(RepoSrc(), "SacredModManager.Core");
        string[] forbidden = ["System.Diagnostics.Process", "Assembly.Load", "NativeLibrary", "DllImport", "LibraryImport", "Activator.CreateInstance", "CSharpScript"];
        foreach (string dir in new[] { "Mods", "Index" })
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(core, dir), "*.cs"))
            {
                string text = File.ReadAllText(file);
                foreach (string f in forbidden)
                {
                    Assert.False(text.Contains(f, StringComparison.Ordinal), Path.GetFileName(file) + " mentions " + f);
                }
            }
        }

        string service = File.ReadAllText(Path.Combine(core, "App", "ModManagerService.cs"));
        Assert.DoesNotContain("Process", service);
    }
}
