using SacredModManager.Core.Index;
using Xunit;

namespace SacredModManager.Tests;

public class IndexImagesTests
{
    private const string Root = "https://example.com/idx/mod-index.json";

    private static async Task<CatalogMod> LoadOne(string modJson)
    {
        var f = new FakeFetcher().Add(Root, "{\"schema\":1,\"download\":{\"url\":\"pkg.zip\"},\"mods\":[" + modJson + "]}");
        IndexCatalog c = await new IndexLoader(f).LoadAsync([new Uri(Root)], TestContext.Current.CancellationToken);
        Assert.True(c.Mods.Count == 1, string.Join(" | ", c.Warnings));
        return c.Mods[0];
    }

    [Fact]
    public async Task ImagesAreZipPathsOrHttpsUrlsAndKeepTheirOrder()
    {
        CatalogMod m = await LoadOne("""{"id":"a","path":"mods/a","images":["mods/a/1.png","https://example.com/2.jpg","mods\\a\\3.png"]}""");
        Assert.Equal(3, m.Images.Count);
        Assert.Equal("mods/a/1.png", m.Images[0].Path);
        Assert.Equal("https://example.com/2.jpg", m.Images[1].Url!.AbsoluteUri);
        Assert.Equal("mods/a/3.png", m.Images[2].Path);
    }

    [Fact]
    public async Task UnsafeOrUnusableImagesAreDropped()
    {
        CatalogMod m = await LoadOne("""{"id":"a","path":"mods/a","images":["../x.png","http://example.com/p.png","C:\\\\p.png","","mods/a/ok.png",null,"/abs.png"]}""");
        Assert.Equal(["mods/a/ok.png", "abs.png"], m.Images.Select(i => i.Path).ToArray()); // a leading slash is the zip root
    }

    [Fact]
    public async Task ThumbnailAloneIsTheOnlyPictureAndTheFirstImageIsTheThumbnailWhenThereIsNone()
    {
        CatalogMod onlyThumb = await LoadOne("""{"id":"a","path":"mods/a","thumbnail":"mods/a/t.png"}""");
        Assert.Equal("mods/a/t.png", Assert.Single(onlyThumb.Images).Path);
        Assert.Equal("mods/a/t.png", onlyThumb.ThumbnailImage!.Path);

        CatalogMod onlyImages = await LoadOne("""{"id":"a","path":"mods/a","images":["https://example.com/1.png","mods/a/2.png"]}""");
        Assert.Equal("https://example.com/1.png", onlyImages.ThumbnailImage!.Url!.AbsoluteUri);

        CatalogMod both = await LoadOne("""{"id":"a","path":"mods/a","thumbnail":"mods/a/t.png","images":["mods/a/1.png"]}""");
        Assert.Equal("mods/a/t.png", both.ThumbnailImage!.Path);
        Assert.Equal("mods/a/1.png", Assert.Single(both.Images).Path);

        CatalogMod none = await LoadOne("""{"id":"a","path":"mods/a"}""");
        Assert.Empty(none.Images);
        Assert.Null(none.ThumbnailImage);
    }

    [Fact]
    public async Task AtMostTwentyFourImagesAreKept()
    {
        string many = string.Join(",", Enumerable.Range(1, 40).Select(i => "\"mods/a/" + i + ".png\""));
        CatalogMod m = await LoadOne("{\"id\":\"a\",\"path\":\"mods/a\",\"images\":[" + many + "]}");
        Assert.Equal(IndexLoader.MaxImagesPerMod, m.Images.Count);
    }
}
