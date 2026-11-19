using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SacredModManager.Core.Links;

/// <summary>Whether the operating system sends <c>gear-mod-manager://</c> links to this executable.</summary>
public enum RegistrationState
{
    /// <summary>This platform has no supported way to register (only Windows and Linux are).</summary>
    Unsupported,

    NotRegistered,

    /// <summary>Another program or another copy of this one is registered.</summary>
    RegisteredElsewhere,

    Registered,
}

/// <summary>The registration could not be written or removed.</summary>
public sealed class UriSchemeException(string message) : Exception(message);

/// <summary>
/// Registers the manager as the handler of its URI scheme for the <b>current user</b> only (no administrator rights): on Windows under
/// <c>HKCU\Software\Classes\&lt;scheme&gt;</c>, on Linux as <c>~/.local/share/applications/&lt;scheme&gt;.desktop</c> plus
/// <c>xdg-mime default</c>. The scheme name is a parameter so that tests can use a throw-away one.
/// </summary>
public static class UriSchemeRegistrar
{
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static RegistrationState Check(string exePath, string scheme = ManagerLink.Scheme)
    {
        if (OperatingSystem.IsWindows())
        {
            return CheckWindows(exePath, scheme);
        }

        if (OperatingSystem.IsLinux())
        {
            return CheckLinux(exePath, scheme);
        }

        return RegistrationState.Unsupported;
    }

    /// <summary>Registers the executable; returns a short description of what was written. Throws <see cref="UriSchemeException"/>.</summary>
    public static string Register(string exePath, string scheme = ManagerLink.Scheme)
    {
        if (!File.Exists(exePath))
        {
            throw new UriSchemeException("the program file was not found: " + exePath);
        }

        if (OperatingSystem.IsWindows())
        {
            return RegisterWindows(exePath, scheme);
        }

        if (OperatingSystem.IsLinux())
        {
            return RegisterLinux(exePath, scheme);
        }

        throw new UriSchemeException("links are only supported on Windows and Linux");
    }

    /// <summary>Removes the registration (nothing happens when there is none).</summary>
    public static void Unregister(string scheme = ManagerLink.Scheme)
    {
        if (OperatingSystem.IsWindows())
        {
            UnregisterWindows(scheme);
        }
        else if (OperatingSystem.IsLinux())
        {
            UnregisterLinux(scheme);
        }
    }

    // ---- what is written (pure, tested) -------------------------------------------------------------------------

    /// <summary>The command of <c>shell\open\command</c>: <c>"C:\path\app.exe" "%1"</c>.</summary>
    public static string WindowsCommand(string exePath) => "\"" + exePath + "\" \"%1\"";

    /// <summary>The text of the Linux <c>.desktop</c> file (Desktop Entry specification: Exec values are quoted, and \, ", ` and $ escaped).</summary>
    public static string DesktopEntry(string exePath, string scheme = ManagerLink.Scheme)
        => string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            "Name=Sacred Mod Manager",
            "Comment=Install and manage Sacred Gear mods",
            "Exec=" + QuoteExec(exePath) + " %u",
            "Terminal=false",
            "NoDisplay=true",
            "Categories=Game;Utility;",
            "MimeType=x-scheme-handler/" + scheme + ";",
            string.Empty);

    public static string DesktopFileName(string scheme = ManagerLink.Scheme) => scheme + ".desktop";

    internal static string QuoteExec(string path)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (char c in path)
        {
            if (c is '\\' or '"' or '`' or '$')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.Append('"').ToString();
    }

    private static bool SamePath(string? a, string? b)
        => a is not null && b is not null && string.Equals(Path.GetFullPath(a.Trim('"')), Path.GetFullPath(b.Trim('"')), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ---- Windows ------------------------------------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static RegistrationState CheckWindows(string exePath, string scheme)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + scheme + @"\shell\open\command");
            if (key?.GetValue(null) is not string command)
            {
                return RegistrationState.NotRegistered;
            }

            return command == WindowsCommand(exePath) || SamePath(FirstQuoted(command), exePath) ? RegistrationState.Registered : RegistrationState.RegisteredElsewhere;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return RegistrationState.NotRegistered;
        }
    }

    private static string? FirstQuoted(string command)
    {
        if (command.Length > 1 && command[0] == '"')
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        int space = command.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? command[..space] : command;
    }

    [SupportedOSPlatform("windows")]
    private static string RegisterWindows(string exePath, string scheme)
    {
        try
        {
            using RegistryKey root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + scheme);
            root.SetValue(null, "URL:Sacred Mod Manager");
            root.SetValue("URL Protocol", string.Empty);
            using (RegistryKey icon = root.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue(null, "\"" + exePath + "\",0");
            }

            using RegistryKey command = root.CreateSubKey(@"shell\open\command");
            command.SetValue(null, WindowsCommand(exePath));
            return @"HKEY_CURRENT_USER\Software\Classes\" + scheme;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            throw new UriSchemeException("the registry could not be written: " + ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void UnregisterWindows(string scheme)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + scheme, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            throw new UriSchemeException("the registry entry could not be removed: " + ex.Message);
        }
    }

    // ---- Linux --------------------------------------------------------------------------------------------------

    private static string ApplicationsDirectory()
    {
        string? data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(data))
        {
            data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(data, "applications");
    }

    private static RegistrationState CheckLinux(string exePath, string scheme)
    {
        string file = Path.Combine(ApplicationsDirectory(), DesktopFileName(scheme));
        if (!File.Exists(file))
        {
            return RegistrationState.NotRegistered;
        }

        try
        {
            string exec = "Exec=" + QuoteExec(exePath) + " %u";
            return File.ReadLines(file).Any(l => l.Trim() == exec) ? RegistrationState.Registered : RegistrationState.RegisteredElsewhere;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RegistrationState.NotRegistered;
        }
    }

    private static string RegisterLinux(string exePath, string scheme)
    {
        string dir = ApplicationsDirectory();
        string file = Path.Combine(dir, DesktopFileName(scheme));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, DesktopEntry(exePath, scheme));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UriSchemeException("the desktop file could not be written: " + ex.Message);
        }

        // best effort: a desktop without these tools still finds the file the next time it reads its applications folder
        RunQuietly("xdg-mime", "default", DesktopFileName(scheme), "x-scheme-handler/" + scheme);
        RunQuietly("update-desktop-database", dir);
        return file;
    }

    private static void UnregisterLinux(string scheme)
    {
        string file = Path.Combine(ApplicationsDirectory(), DesktopFileName(scheme));
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
                RunQuietly("update-desktop-database", ApplicationsDirectory());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UriSchemeException("the desktop file could not be removed: " + ex.Message);
        }
    }

    private static void RunQuietly(string program, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string a in arguments)
            {
                info.ArgumentList.Add(a);
            }

            using Process? p = Process.Start(info);
            p?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // the tool is not installed
        }
    }
}
