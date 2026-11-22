using System.Net;
using System.Text;
using SacredModManager.Core.App;
using SacredModManager.Core.Index;
using SacredModManager.Core.Mods;
using Xunit;

namespace SacredModManager.Tests;

/// <summary>
/// Loose files: a plugin file that comes straight from an https address, pinned by sha256, instead of from an archive; and the refusal of programs and
/// scripts as plugin targets. Nothing here uses the network: files are served by a fake fetcher, the redirect rule is tested against a stub HTTP handler.
/// </summary>
public sealed class LooseFileTests : IDisposable
{
    private const string IndexUrl = "https://example.com/repo/mod-index.json";
    private const string IniUrl = "https://raw.githubusercontent.com/owner/repo/abc123/docs/dsoal/alsoft-speakers.ini";
    private static readonly byte[] Ini = Encoding.UTF8.GetBytes("[general]\nstereo-mode=speakers\nstereo-encoding=basic\n");

    private readonly TempDir _root = new();
    private readonly FakeFetcher _fetcher = new();
    private readonly ModManagerService _service;

    public LooseFileTests()
    {
        var paths = new AppPaths(Path.Combine(_root.Path, "home"));
        _service = new ModManagerService(paths, _fetcher);
        Game = _root.Combine("game");
        Directory.CreateDirectory(Game);
        Store = new ModStore(Game, paths.Backups, _root.Combine("tools"));
        _fetcher.Add(IniUrl, Ini);
    }

    private string Game { get; }
    private ModStore Store { get; }
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _root.Dispose();

    private static string Entry(string id, string file, string extra = "") => "{\"id\":\"" + id + "\",\"kind\":\"plugin\"" + extra + ",\"install\":{\"files\":[" + file + "]}}";

    private static string File_(string url, string? sha, string to)
        => "{\"url\":\"" + url + "\"" + (sha is null ? string.Empty : ",\"sha256\":\"" + sha + "\"") + ",\"to\":\"" + to + "\"}";

    private async Task<IndexCatalog> Load(params string[] entries)
    {
        var f = new FakeFetcher().Add(IndexUrl, "{\"schema\":1,\"download\":{\"url\":\"unused.zip\"},\"mods\":[" + string.Join(",", entries) + "]}");
        return await new IndexLoader(f, "win-x64").LoadAsync([new Uri(IndexUrl)], Ct);
    }

    private async Task<CatalogMod> One(string file, string extra = "")
    {
        IndexCatalog c = await Load(Entry("spk", file, extra));
        Assert.Empty(c.Warnings);
        return c.Mods.Single();
    }

    private async Task<BatchItem> Install(CatalogMod mod, Func<PluginConflictException, Task<bool>>? resolve = null)
        => (await _service.InstallPlanAsync(Store, ModManagerService.Plan([mod], [mod], Store.Registry.Load().Mods.Keys), null, null, Ct, resolve)).Last();

    // ---- the index syntax --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALooseFileEntryNeedsNoArchiveAndKeepsUrlHashAndTarget()
    {
        CatalogMod m = await One(File_(IniUrl, Pack.Sha256(Ini), "alsoft.ini"));
        LooseFile f = Assert.Single(m.LooseFiles);
        Assert.Equal(IniUrl, f.Url.AbsoluteUri);
        Assert.Equal(Pack.Sha256(Ini), f.Sha256);
        Assert.Equal("alsoft.ini", f.To);
        Assert.Empty(m.InstallFiles);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("short")]
    [InlineData("nothex")]
    public async Task ALooseFileWithoutAValidSha256IsSkipped(string kind)
    {
        string? sha = kind switch { "missing" => null, "short" => "abcd", _ => new string('z', 64) };
        IndexCatalog c = await Load(Entry("spk", File_(IniUrl, sha, "alsoft.ini")));
        Assert.Empty(c.Mods);
        Assert.Contains(c.Warnings, w => w.Contains("sha256", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://raw.githubusercontent.com/o/r/c/a.ini")]
    [InlineData("ftp://example.com/a.ini")]
    [InlineData("file:///C:/a.ini")]
    [InlineData("a.ini")]
    public async Task ALooseFileMustBeAnAbsoluteHttpsAddress(string url)
    {
        IndexCatalog c = await Load(Entry("spk", File_(url, Pack.Sha256(Ini), "alsoft.ini")));
        Assert.Empty(c.Mods);
        Assert.Contains(c.Warnings, w => w.Contains("https", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../alsoft.ini")]
    [InlineData("a/../../alsoft.ini")]
    [InlineData("/alsoft.ini")]
    [InlineData("C:/alsoft.ini")]
    [InlineData("C:\\\\alsoft.ini")]
    [InlineData("*.ini")]
    [InlineData("")]
    public async Task ALooseFileNeedsATargetBelowTheGameFolder(string to)
    {
        IndexCatalog c = await Load(Entry("spk", File_(IniUrl, Pack.Sha256(Ini), to)));
        Assert.Empty(c.Mods);
        Assert.Contains(c.Warnings, w => w.Contains("target", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("run.exe")]
    [InlineData("setup.msi")]
    [InlineData("go.bat")]
    [InlineData("go.cmd")]
    [InlineData("x.ps1")]
    [InlineData("x.sh")]
    [InlineData("x.vbs")]
    [InlineData("x.js")]
    [InlineData("x.lnk")]
    [InlineData("x.scr")]
    [InlineData("x.com")]
    [InlineData("x.jar")]
    [InlineData("sub/X.PS1")]
    [InlineData("go.bat.")]
    public async Task ALooseFileCanNeverBeAProgramOrScript(string to)
    {
        IndexCatalog c = await Load(Entry("spk", File_(IniUrl, Pack.Sha256(Ini), to)));
        Assert.Empty(c.Mods);
        Assert.Contains(c.Warnings, w => w.Contains("program or script", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LooseFilesCannotBeMixedWithArchiveFilesOrAnArchiveDownloadOfTheEntry()
    {
        IndexCatalog mixed = await Load(Entry("a", File_(IniUrl, Pack.Sha256(Ini), "alsoft.ini") + ",\"x.dll\""));
        Assert.Empty(mixed.Mods);
        IndexCatalog withDownload = await Load(Entry("b", File_(IniUrl, Pack.Sha256(Ini), "alsoft.ini"), ",\"download\":{\"url\":\"p.zip\"}"));
        Assert.Empty(withDownload.Mods);
        Assert.All([mixed, withDownload], c => Assert.Single(c.Warnings));
    }

    [Fact]
    public async Task UnknownFieldsOfALooseFileStayIgnored()
    {
        IndexCatalog c = await Load(Entry("spk", "{\"url\":\"" + IniUrl + "\",\"sha256\":\"" + Pack.Sha256(Ini) + "\",\"to\":\"alsoft.ini\",\"future\":1}"));
        Assert.Single(c.Mods);
        Assert.Empty(c.Warnings);
    }

    // ---- the question before installing -----------------------------------------------------------------------------

    [Fact]
    public async Task ThePreviewListsTheLooseFileWithUrlHashAndTarget()
    {
        CatalogMod m = await One(File_(IniUrl, Pack.Sha256(Ini), "alsoft.ini"));
        string[] lines = InstallPreview.Describe(m, "T").ToArray();
        Assert.Contains(lines, l => l.Contains(IniUrl, StringComparison.Ordinal) && l.Contains(Pack.Sha256(Ini), StringComparison.Ordinal) && l.Contains("-> alsoft.ini", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("from ", StringComparison.Ordinal));
        Assert.Contains("listed by " + IndexUrl, lines);
    }

    // ---- installing ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task InstallWritesTheFileRecordsOwnerAndHashAndRemovalDeletesIt()
    {
        CatalogMod m = await One(File_(IniUrl, Pack.Sha256(Ini), "alsoft.ini"));
        BatchItem r = await Install(m);
        Assert.Null(r.Error);
        Assert.Equal(Ini, File.ReadAllBytes(Path.Combine(Game, "alsoft.ini")));
        ModStore.FileOwner owner = Store.OwnerOf("alsoft.ini")!;
        Assert.Equal("spk", owner.Id);
        Assert.Equal(ItemKind.Plugin, owner.Kind);
        Assert.Equal(Pack.Sha256(Ini), owner.Sha256);
        Assert.Contains(IniUrl, Store.Registry.Load().Mods["spk"].PackageUrl, StringComparison.Ordinal);

        InstalledMod installed = (await Store.ScanAsync(Ct)).Single(x => x.Id == "spk");
        Store.Uninstall(installed, removeData: false, forgetSettings: false);
        Assert.False(File.Exists(Path.Combine(Game, "alsoft.ini")));
    }

    [Fact]
    public async Task AFileThePlayerHadIsSavedAndPutBackOnRemoval()
    {
        File.WriteAllText(Path.Combine(Game, "alsoft.ini"), "mine");
        CatalogMod m = await One(File_(IniUrl, Pack.Sha256(Ini), "alsoft.ini"));
        Assert.Null((await Install(m)).Error);
        Assert.Equal(Ini, File.ReadAllBytes(Path.Combine(Game, "alsoft.ini")));
        Store.Uninstall((await Store.ScanAsync(Ct)).Single(x => x.Id == "spk"), removeData: false, forgetSettings: false);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(Game, "alsoft.ini")));
    }

    [Fact]
    public async Task AHashMismatchInstallsNothing()
    {
        CatalogMod m = await One(File_(IniUrl, Pack.Sha256(Encoding.UTF8.GetBytes("other")), "alsoft.ini"));
        BatchItem r = await Install(m);
        Assert.Contains("sha256", r.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Game, "alsoft.ini")));
        Assert.Empty(Store.Registry.Load().Mods);
    }

    [Fact]
    public void TheStoreRefusesAMissingHashEvenIfACallerSkipsTheLoader()
    {
        var f = new LooseFileData("alsoft.ini", Ini, string.Empty, IniUrl);
        Assert.Contains("no sha256", Assert.Throws<StoreException>(() => Store.InstallLoosePlugin("spk", "1", string.Empty, [f], null)).Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Game, "alsoft.ini")));
    }

    [Fact]
    public async Task AFileOverTheSizeCapIsRefused()
    {
        byte[] big = new byte[(int)ModStore.MaxLooseFileBytes + 1];
        _fetcher.Add(IniUrl, big);
        CatalogMod m = await One(File_(IniUrl, Pack.Sha256(big), "alsoft.ini"));
        Assert.NotNull((await Install(m)).Error); // the fetcher enforces the cap while reading
        Assert.False(File.Exists(Path.Combine(Game, "alsoft.ini")));

        var data = new LooseFileData("alsoft.ini", big, Pack.Sha256(big), IniUrl); // and the store checks it again
        Assert.Contains("larger than", Assert.Throws<StoreException>(() => Store.InstallLoosePlugin("spk", "1", string.Empty, [data], null)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStoreRefusesProgramTargetsForLooseFiles()
    {
        var data = new LooseFileData("tool.exe", Ini, Pack.Sha256(Ini), IniUrl);
        Assert.Contains("program or script", Assert.Throws<StoreException>(() => Store.InstallLoosePlugin("spk", "1", string.Empty, [data], null)).Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Game, "tool.exe")));
    }

    [Fact]
    public void TheStoreRefusesTargetsOutsideTheGameFolder()
    {
        foreach (string to in new[] { "../x.ini", "..\\x.ini", "/x.ini", "C:/x.ini" })
        {
            var data = new LooseFileData(to, Ini, Pack.Sha256(Ini), IniUrl);
            Assert.ThrowsAny<Exception>(() => Store.InstallLoosePlugin("spk", "1", string.Empty, [data], null));
        }

        Assert.False(File.Exists(Path.Combine(_root.Path, "x.ini")));
    }

    [Fact]
    public async Task ALooseFileOfAnotherPluginConflictsAndTheAnswerNoChangesNothing()
    {
        CatalogMod a = await One(File_(IniUrl, Pack.Sha256(Ini), "alsoft.ini"));
        Assert.Null((await Install(a)).Error);

        byte[] hp = Encoding.UTF8.GetBytes("HP");
        const string HpUrl = "https://raw.githubusercontent.com/owner/repo/abc123/hp.ini";
        _fetcher.Add(HpUrl, hp);
        IndexCatalog c = await Load(Entry("hp", File_(HpUrl, Pack.Sha256(hp), "alsoft.ini")));
        CatalogMod b = c.Mods.Single();

        PluginConflictException? seen = null;
        BatchItem no = await Install(b, e => { seen = e; return Task.FromResult(false); });
        Assert.True(no.Aborted);
        Assert.Equal("spk", seen!.ConflictingId);
        Assert.Equal(["alsoft.ini"], seen.Files);
        Assert.Equal(Ini, File.ReadAllBytes(Path.Combine(Game, "alsoft.ini")));

        BatchItem yes = await Install(b, _ => Task.FromResult(true));
        Assert.Null(yes.Error);
        Assert.Equal(hp, File.ReadAllBytes(Path.Combine(Game, "alsoft.ini")));
        Assert.Equal("hp", Store.OwnerOf("alsoft.ini")!.Id);
    }

    // ---- redirects ---------------------------------------------------------------------------------------------------

    private sealed class StubHandler(Func<Uri, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(answer(request.RequestUri!));
        }
    }

    private static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    [Fact]
    public async Task ARedirectToHttpIsRefusedAndNeverRequested()
    {
        var handler = new StubHandler(u => Redirect("http://evil.example/a.ini"));
        using var fetcher = new Fetcher(new HttpClient(handler), []);
        FetchException ex = await Assert.ThrowsAsync<FetchException>(() => fetcher.GetBytesAsync(new Uri(IniUrl), 1024, Ct));
        Assert.Contains("not https", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ARedirectToHttpsIsFollowedAndRedirectLoopsStop()
    {
        var ok = new StubHandler(u => u.Host == "raw.githubusercontent.com" ? Redirect("https://cdn.example/a.ini") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Ini) });
        using (var fetcher = new Fetcher(new HttpClient(ok), []))
        {
            Assert.Equal(Ini, await fetcher.GetBytesAsync(new Uri(IniUrl), 1024, Ct));
        }

        var loop = new StubHandler(u => Redirect("https://example.com/again"));
        using var looping = new Fetcher(new HttpClient(loop), []);
        FetchException ex = await Assert.ThrowsAsync<FetchException>(() => looping.GetBytesAsync(new Uri(IniUrl), 1024, Ct));
        Assert.Contains("too many redirects", ex.Message, StringComparison.Ordinal);
    }

    // ---- programs and scripts in archive plugins ---------------------------------------------------------------------

    private static readonly Uri Idx = new("https://example.com/mod-index.json");
    private static readonly Uri Pkg = new("https://example.com/p.zip");

    private static CatalogMod Archive(string id, (string, string)[] files)
        => new() { Id = id, Kind = ItemKind.Plugin, Path = "p", DownloadUrl = Pkg, IndexUrl = Idx, InstallFiles = files };

    private void Zip(params string[] names) => _fetcher.Add(Pkg.AbsoluteUri, Pack.Zip(names.Select(n => ("p/" + n, "data")).ToArray()));

    public static TheoryData<string> Executables => new() { "a.exe", "a.msi", "a.bat", "a.cmd", "a.ps1", "a.sh", "a.vbs", "a.js", "a.lnk", "a.scr", "a.com", "a.jar", "a.hta", "a.reg", "a.wsf" };

    [Theory]
    [MemberData(nameof(Executables))]
    public async Task AnArchivePluginDoesNotWriteAProgramOrScriptForEveryFileAFolderOrAWildcard(string name)
    {
        Zip(name, "ok.dll");
        Assert.Contains("program or script", (await Install(Archive("every", []))).Error, StringComparison.Ordinal);
        Assert.Contains("program or script", (await Install(Archive("wild", [("*", "")]))).Error, StringComparison.Ordinal);
        Assert.Contains("program or script", (await Install(Archive("wild2", [("*." + name.Split('.')[1], "")]))).Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Game, name)));
        Assert.False(File.Exists(Path.Combine(Game, "ok.dll")));
        Assert.Empty(Store.Registry.Load().Mods);
    }

    [Fact]
    public async Task AProgramInAFolderEntryIsRefusedButANamedOneIsAllowed()
    {
        Zip("sub/run.bat", "ok.dll");
        Assert.Contains("program or script", (await Install(Archive("folder", [("sub", "sub")]))).Error, StringComparison.Ordinal);
        Assert.Null((await Install(Archive("named", [("sub/run.bat", "run.bat"), ("ok.dll", "ok.dll")]))).Error);
        Assert.True(File.Exists(Path.Combine(Game, "run.bat")));
        Assert.True(File.Exists(Path.Combine(Game, "ok.dll")));
    }

    [Fact]
    public async Task DllsAndDataFilesStayAllowedForEveryFile()
    {
        Zip("dsound.dll", "alsoft.ini", "readme.txt", "data/x.bin");
        Assert.Null((await Install(Archive("all", []))).Error);
        Assert.True(File.Exists(Path.Combine(Game, "data", "x.bin")));
    }

    [Theory]
    [InlineData("a.exe", true)]
    [InlineData("dir\\A.EXE", true)]
    [InlineData("a.exe.", true)]
    [InlineData("a.dll", false)]
    [InlineData("alsoft.ini", false)]
    [InlineData("exe", false)]
    [InlineData(".exe", false)]
    public void ExecutableTargetsRecognizesByExtension(string path, bool expected) => Assert.Equal(expected, ExecutableTargets.IsExecutable(path));

    // ---- a download that wraps one more archive ------------------------------------------------------------------------

    [Fact]
    public async Task ADownloadWithAnInnerArchiveInstallsFromTheInnerOneAndShowsItInTheQuestion()
    {
        byte[] inner = Pack.Zip(("DSOAL/Win32/dsound.dll", "D"), ("DSOAL/Win32/dsoal-aldrv.dll", "A"), ("DSOAL+HRTF/Win32/alsoft.ini", "HRTF"));
        byte[] outer = Pack.Zip(("DSOAL_r697.zip", inner));
        const string Url = "https://example.com/dsoal/DSOAL_r697.zip";
        _fetcher.Add(Url, outer);
        var f = new FakeFetcher().Add(IndexUrl,
            "{\"schema\":1,\"download\":{\"url\":\"" + Url + "\",\"sha256\":\"" + Pack.Sha256(outer) + "\",\"inner\":\"DSOAL_r697.zip\"},\"mods\":["
            + "{\"id\":\"base\",\"kind\":\"plugin\",\"path\":\"DSOAL/Win32\",\"install\":{\"files\":[\"dsound.dll\",\"dsoal-aldrv.dll\"]}},"
            + "{\"id\":\"hp\",\"kind\":\"plugin\",\"path\":\"DSOAL+HRTF/Win32\",\"requires\":[\"base\"],\"install\":{\"files\":[\"alsoft.ini\"]}}]}");
        IndexCatalog c = await new IndexLoader(f, "win-x64").LoadAsync([new Uri(IndexUrl)], Ct);
        Assert.Empty(c.Warnings);
        CatalogMod hp = c.Mods.Single(m => m.Id == "hp");
        Assert.Contains(InstallPreview.Describe(hp, "T"), l => l.Contains("'DSOAL_r697.zip' inside it", StringComparison.Ordinal));

        Assert.All(await _service.InstallPlanAsync(Store, ModManagerService.Plan([hp], c.Mods, []), null, null, Ct), r => Assert.Null(r.Error));
        Assert.Equal("D", File.ReadAllText(Path.Combine(Game, "dsound.dll")));
        Assert.Equal("HRTF", File.ReadAllText(Path.Combine(Game, "alsoft.ini")));
        Assert.Equal("base", Store.OwnerOf("dsound.dll")!.Id);

        // a download that does not hold the named inner archive is an error and changes nothing
        var bad = new FakeFetcher().Add(IndexUrl, "{\"schema\":1,\"download\":{\"url\":\"" + Url + "\",\"inner\":\"nope.zip\"},\"mods\":[{\"id\":\"x\",\"kind\":\"plugin\",\"path\":\"DSOAL/Win32\",\"install\":{\"files\":[\"dsound.dll\"]}}]}");
        CatalogMod x = (await new IndexLoader(bad, "win-x64").LoadAsync([new Uri(IndexUrl)], Ct)).Mods.Single();
        Assert.Contains("nope.zip", (await Install(x)).Error, StringComparison.Ordinal);
    }
}
