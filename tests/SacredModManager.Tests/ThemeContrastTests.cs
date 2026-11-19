using System.Xml.Linq;
using Xunit;

namespace SacredModManager.Tests;

/// <summary>
/// The palette lives in <c>src/SacredModManager/Themes/Theme.axaml</c> (Light and Dark variants). These tests read that file (no UI) and check the
/// WCAG contrast of every text/background pair the views use (4.5 for text), and that the dark variant is layered grey, never black.
/// </summary>
public class ThemeContrastTests
{
    private static readonly XNamespace Av = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] Surfaces = ["AppWindowBrush", "AppPanelBrush", "AppCardBrush", "AppRaisedBrush", "AppInputBrush"];

    public static IEnumerable<object[]> Variants() => [["Light"], ["Dark"]];

    private static Dictionary<string, (byte R, byte G, byte B)> Palette(string variant)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SacredModManager.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        XDocument doc = XDocument.Load(Path.Combine(dir!, "src", "SacredModManager", "Themes", "Theme.axaml"));
        XElement v = doc.Descendants(Av + "ResourceDictionary").First(e => (string?)e.Attribute(X + "Key") == variant);
        var map = new Dictionary<string, (byte, byte, byte)>();
        foreach (XElement e in v.Elements())
        {
            string? key = (string?)e.Attribute(X + "Key");
            string? color = e.Name.LocalName == "SolidColorBrush" ? (string?)e.Attribute("Color") : e.Name.LocalName == "Color" ? e.Value.Trim() : null;
            if (key is not null && color is { Length: 7 } && color[0] == '#')
            {
                map[key] = (Convert.ToByte(color.Substring(1, 2), 16), Convert.ToByte(color.Substring(3, 2), 16), Convert.ToByte(color.Substring(5, 2), 16));
            }
        }

        return map;
    }

    private static double Lum((byte R, byte G, byte B) c)
    {
        static double Ch(byte v)
        {
            double x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Ch(c.R)) + (0.7152 * Ch(c.G)) + (0.0722 * Ch(c.B));
    }

    private static double Contrast((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        double la = Lum(a), lb = Lum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Fact]
    public void FormulaMatchesTheWcagExamples()
    {
        Assert.Equal(21.0, Contrast((0, 0, 0), (255, 255, 255)), 3);
        Assert.Equal(4.54, Contrast((118, 118, 118), (255, 255, 255)), 2);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void TextColorsReadOnEverySurface(string variant)
    {
        var p = Palette(variant);
        foreach (string text in new[] { "AppTextBrush", "AppTextSecondaryBrush", "AppAccentBrush", "AppWarnBrush", "AppErrBrush", "AppOkBrush" })
        {
            foreach (string surface in Surfaces)
            {
                Assert.True(Contrast(p[text], p[surface]) >= 4.5, $"{variant}: {text} on {surface} = {Contrast(p[text], p[surface]):0.00}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void ChipsBannersAndButtonsReadOnTheirFill(string variant)
    {
        var p = Palette(variant);
        (string Text, string Fill)[] pairs =
        [
            ("AppOkBrush", "AppOkFillBrush"), ("AppWarnBrush", "AppWarnFillBrush"), ("AppErrBrush", "AppErrFillBrush"), ("AppAccentBrush", "AppAccentFillBrush"),
            ("AppTextBrush", "AppWarnFillBrush"), ("AppTextBrush", "AppAccentFillBrush"), ("AppTextBrush", "AppErrFillBrush"),
            ("AppPrimaryTextBrush", "AppPrimaryBrush"), ("AppPrimaryTextBrush", "AppPrimaryHoverBrush"), ("AppPrimaryTextBrush", "AppDangerBrush"),
        ];
        foreach ((string text, string fill) in pairs)
        {
            Assert.True(Contrast(p[text], p[fill]) >= 4.5, $"{variant}: {text} on {fill} = {Contrast(p[text], p[fill]):0.00}");
        }
    }

    [Fact]
    public void DarkThemeIsLayeredGreyNeverBlack()
    {
        var p = Palette("Dark");
        foreach (string s in Surfaces)
        {
            Assert.True(Lum(p[s]) > 0.012, $"{s} is too dark");
            Assert.True(Lum(p[s]) < 0.06, $"{s} is not dark");
        }

        // layers get lighter: window < panel < card < raised
        Assert.True(Lum(p["AppWindowBrush"]) < Lum(p["AppPanelBrush"]));
        Assert.True(Lum(p["AppPanelBrush"]) < Lum(p["AppCardBrush"]));
        Assert.True(Lum(p["AppCardBrush"]) < Lum(p["AppRaisedBrush"]));
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void EveryBrushOfOneVariantExistsInTheOther(string variant)
    {
        string other = variant == "Dark" ? "Light" : "Dark";
        foreach (string key in Palette(variant).Keys.Where(k => k.StartsWith("App", StringComparison.Ordinal)))
        {
            Assert.True(Palette(other).ContainsKey(key), $"{key} is missing in {other}");
        }
    }
}
