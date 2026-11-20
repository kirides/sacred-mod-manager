using System.Diagnostics;
using Microsoft.Win32;

namespace SacredModManager.Core.App;

/// <summary>Finding and checking the Sacred Gold folder.</summary>
public static class GameLocator
{
    public const string DefaultFolder = @"B:\Spiele\GOG Games\sacred gold GOG";

    public static bool IsGameFolder(string? folder)
        => !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && File.Exists(Path.Combine(folder, "Sacred.exe"));

    /// <summary>The first folder that holds Sacred.exe: the saved one, the default, the GOG registry entry, common install places.</summary>
    public static string? Find(string? saved)
    {
        foreach (string? candidate in Candidates(saved))
        {
            if (IsGameFolder(candidate))
            {
                return Path.GetFullPath(candidate!);
            }
        }

        return null;
    }

    private static IEnumerable<string?> Candidates(string? saved)
    {
        yield return saved;
        yield return DefaultFolder;
        if (OperatingSystem.IsWindows())
        {
            yield return FromRegistry();
        }

        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"C:\GOG Games", @"D:\GOG Games", @"C:\Games", @"D:\Games" })
        {
            if (root.Length == 0)
            {
                continue;
            }

            yield return Path.Combine(root, "GOG Galaxy", "Games", "Sacred Gold");
            yield return Path.Combine(root, "Sacred Gold");
            yield return Path.Combine(root, "Sacred Gold GOG");
            yield return Path.Combine(root, "Steam", "steamapps", "common", "Sacred Gold");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? FromRegistry()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games\1207658688");
            return key?.GetValue("path") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Set by the screenshot mode: no process lookup, the game is reported as not running.</summary>
    public static bool SuppressProcessCheck { get; set; }

    /// <summary>
    /// Is the Sacred of <paramref name="folder"/> running? (Then its files are locked and it would overwrite <c>gear_mods.json</c>.)
    /// A Sacred.exe started from another folder (a test copy) does not count.
    /// </summary>
    public static bool IsGameRunning(string folder)
    {
        if (SuppressProcessCheck)
        {
            return false;
        }

        var paths = new List<string?>();
        foreach (Process p in Process.GetProcessesByName("Sacred"))
        {
            try
            {
                paths.Add(p.MainModule?.FileName);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                paths.Add(null); // not readable: cannot tell, assume it is ours
            }
            finally
            {
                p.Dispose();
            }
        }

        return IsRunningFrom(folder, paths);
    }

    /// <summary>The testable core: does one of the Sacred.exe paths (null: unknown) lie in <paramref name="folder"/>?</summary>
    public static bool IsRunningFrom(string folder, IEnumerable<string?> exePaths)
    {
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (string? exe in exePaths)
        {
            if (exe is null)
            {
                return true;
            }

            string? dir = Path.GetDirectoryName(Path.GetFullPath(exe))?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Starts the game from its own folder; false when it cannot be started.</summary>
    public static bool Launch(string folder, out string? error)
    {
        error = null;
        try
        {
            string exe = Path.Combine(folder, "Sacred.exe");
            using Process? p = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = folder, UseShellExecute = true });
            return p is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }
}
