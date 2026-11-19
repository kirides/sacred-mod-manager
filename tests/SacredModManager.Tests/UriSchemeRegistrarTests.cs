using Microsoft.Win32;
using SacredModManager.Core.Links;
using Xunit;

namespace SacredModManager.Tests;

public class UriSchemeRegistrarTests
{
    private static string NewScheme() => "gear-mod-manager-test-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public void WindowsCommandQuotesTheExecutableAndTheArgument()
    {
        Assert.Equal("\"C:\\Program Files\\Sacred Mod Manager\\smm.exe\" \"%1\"", UriSchemeRegistrar.WindowsCommand(@"C:\Program Files\Sacred Mod Manager\smm.exe"));
        Assert.Equal("\"x\" \"%1\"", UriSchemeRegistrar.WindowsCommand("x"));
    }

    [Theory]
    [InlineData("/opt/smm/smm", "\"/opt/smm/smm\"")]
    [InlineData("/opt/my apps/smm", "\"/opt/my apps/smm\"")]
    [InlineData("/opt/a\"b/smm", "\"/opt/a\\\"b/smm\"")]
    [InlineData("/opt/$HOME/smm", "\"/opt/\\$HOME/smm\"")]
    [InlineData("/opt/a`b/smm", "\"/opt/a\\`b/smm\"")]
    [InlineData("/opt/a\\b/smm", "\"/opt/a\\\\b/smm\"")]
    [InlineData("/opt/%u/smm", "\"/opt/%u/smm\"")]
    [InlineData("", "\"\"")]
    public void QuoteExecEscapesTheSpecialCharacters(string path, string expected)
        => Assert.Equal(expected, UriSchemeRegistrar.QuoteExec(path));

    [Fact]
    public void QuoteExecEscapesEveryOccurrence()
        => Assert.Equal("\"\\\\\\\\\\$\\$\"", UriSchemeRegistrar.QuoteExec("\\\\$$"));

    [Fact]
    public void DesktopEntryHasTheExpectedLines()
    {
        string text = UriSchemeRegistrar.DesktopEntry("/opt/my apps/smm");
        string[] lines = text.Split('\n');
        Assert.Equal("[Desktop Entry]", lines[0]);
        Assert.Contains("Type=Application", lines);
        Assert.Contains("Exec=\"/opt/my apps/smm\" %u", lines);
        Assert.Contains("Terminal=false", lines);
        Assert.Contains("NoDisplay=true", lines);
        Assert.Contains("MimeType=x-scheme-handler/gear-mod-manager;", lines);
        Assert.DoesNotContain('\r', text);
    }

    [Fact]
    public void DesktopEntryEndsWithExactlyOneNewline()
    {
        string text = UriSchemeRegistrar.DesktopEntry("/x");
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopEntryUsesTheGivenScheme()
    {
        string text = UriSchemeRegistrar.DesktopEntry("/x", "my-scheme");
        Assert.Contains("MimeType=x-scheme-handler/my-scheme;\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("gear-mod-manager", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopEntryQuotesDangerousPaths()
    {
        string text = UriSchemeRegistrar.DesktopEntry("/home/u/a \"b\" $c `d` \\e/smm");
        Assert.Contains("Exec=\"/home/u/a \\\"b\\\" \\$c \\`d\\` \\\\e/smm\" %u\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopFileNames()
    {
        Assert.Equal("gear-mod-manager.desktop", UriSchemeRegistrar.DesktopFileName());
        Assert.Equal("x.desktop", UriSchemeRegistrar.DesktopFileName("x"));
    }

    [Fact]
    public void IsSupportedMatchesThePlatform()
        => Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), UriSchemeRegistrar.IsSupported);

    [Fact]
    public void RegisterWithAMissingExecutableThrows()
    {
        using var dir = new TempDir();
        string scheme = NewScheme();
        Assert.Throws<UriSchemeException>(() => UriSchemeRegistrar.Register(dir.Combine("missing.exe"), scheme));
        Assert.Equal(UriSchemeRegistrar.IsSupported ? RegistrationState.NotRegistered : RegistrationState.Unsupported,
            UriSchemeRegistrar.Check(dir.Combine("missing.exe"), scheme));
    }

    [Fact]
    public void WindowsRoundTripWithAThrowAwayScheme()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string scheme = NewScheme();
        string keyPath = @"Software\Classes\" + scheme;
        string exe = Environment.ProcessPath!;
        using var dir = new TempDir();
        string other = dir.Combine("other.exe");
        File.WriteAllText(other, "x");
        try
        {
            Assert.Equal(RegistrationState.NotRegistered, UriSchemeRegistrar.Check(exe, scheme));

            string description = UriSchemeRegistrar.Register(exe, scheme);
            Assert.Contains(scheme, description, StringComparison.Ordinal);
            Assert.Equal(RegistrationState.Registered, UriSchemeRegistrar.Check(exe, scheme));
            Assert.Equal(RegistrationState.RegisteredElsewhere, UriSchemeRegistrar.Check(other, scheme));
            Assert.Equal(RegistrationState.Registered, UriSchemeRegistrar.Check(exe.ToUpperInvariant(), scheme));

            using (RegistryKey root = Registry.CurrentUser.OpenSubKey(keyPath)!)
            {
                Assert.NotNull(root);
                Assert.Contains("URL Protocol", root.GetValueNames());
                Assert.Equal(string.Empty, root.GetValue("URL Protocol"));
            }

            using (RegistryKey command = Registry.CurrentUser.OpenSubKey(keyPath + @"\shell\open\command")!)
            {
                Assert.Equal(UriSchemeRegistrar.WindowsCommand(exe), command.GetValue(null));
            }

            using (RegistryKey icon = Registry.CurrentUser.OpenSubKey(keyPath + @"\DefaultIcon")!)
            {
                Assert.Equal("\"" + exe + "\",0", icon.GetValue(null));
            }

            // idempotent
            UriSchemeRegistrar.Register(exe, scheme);
            Assert.Equal(RegistrationState.Registered, UriSchemeRegistrar.Check(exe, scheme));

            // registering another executable takes over
            UriSchemeRegistrar.Register(other, scheme);
            Assert.Equal(RegistrationState.Registered, UriSchemeRegistrar.Check(other, scheme));
            Assert.Equal(RegistrationState.RegisteredElsewhere, UriSchemeRegistrar.Check(exe, scheme));

            UriSchemeRegistrar.Unregister(scheme);
            Assert.Equal(RegistrationState.NotRegistered, UriSchemeRegistrar.Check(exe, scheme));
            Assert.Null(Registry.CurrentUser.OpenSubKey(keyPath));

            UriSchemeRegistrar.Unregister(scheme);
            Assert.Null(Registry.CurrentUser.OpenSubKey(keyPath));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void WindowsExecutableWithSpacesIsRecognizedAsRegistered()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string scheme = NewScheme();
        using var dir = new TempDir();
        string exe = dir.Combine("Program Files", "my app.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "x");
        try
        {
            UriSchemeRegistrar.Register(exe, scheme);
            Assert.Equal(RegistrationState.Registered, UriSchemeRegistrar.Check(exe, scheme));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + scheme, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void LinuxDesktopFileRoundTrip()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only");
        using var dir = new TempDir();
        string? old = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", dir.Combine("data"));
        try
        {
            string scheme = NewScheme();
            string exe = dir.Combine("my app", "smm");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "x");
            string other = dir.Combine("other");
            File.WriteAllText(other, "x");
            string desktop = dir.Combine("data", "applications", scheme + ".desktop");

            Assert.Equal(RegistrationState.NotRegistered, UriSchemeRegistrar.Check(exe, scheme));

            Assert.Equal(desktop, UriSchemeRegistrar.Register(exe, scheme));
            Assert.Equal(UriSchemeRegistrar.DesktopEntry(exe, scheme), File.ReadAllText(desktop));
            Assert.Equal(RegistrationState.Registered, UriSchemeRegistrar.Check(exe, scheme));
            Assert.Equal(RegistrationState.RegisteredElsewhere, UriSchemeRegistrar.Check(other, scheme));

            UriSchemeRegistrar.Register(exe, scheme);
            Assert.Equal(RegistrationState.Registered, UriSchemeRegistrar.Check(exe, scheme));

            UriSchemeRegistrar.Unregister(scheme);
            Assert.False(File.Exists(desktop));
            Assert.Equal(RegistrationState.NotRegistered, UriSchemeRegistrar.Check(exe, scheme));
            UriSchemeRegistrar.Unregister(scheme);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", old);
        }
    }
}
