using System.Diagnostics;
using System.Globalization;
using Lua;

namespace SacredModManager.Core.Mods;

/// <summary>Why a mod.lua could not be read.</summary>
public sealed class ManifestException(string message) : Exception(message);

/// <summary>
/// Reads the metadata of a mod from the text of its <c>mod.lua</c> in a Lua state that has <b>nothing</b>: no libraries at all (so no
/// <c>io</c>, <c>os</c>, <c>require</c>, <c>load*</c>, <c>dofile</c>, <c>string</c> ...), an empty global table, a hook that counts
/// every instruction and the memory the thread allocates, a wall clock limit and a size limit on the source. Sacred Gear itself
/// runs mod.lua without globals ("it can only hold values"), so a manifest that works in the game works here.
/// </summary>
public static class LuaManifestReader
{
    public const int MaxSourceChars = 256 * 1024;
    public const long MaxInstructions = 2_000_000;
    public const long MaxAllocatedBytes = 48L * 1024 * 1024;
    public static readonly TimeSpan MaxTime = TimeSpan.FromSeconds(2);

    private const int MaxTableEntries = 4000;

    public static ModManifest Read(string luaSource, string chunkName = "mod.lua")
        => ReadAsync(luaSource, chunkName).AsTask().GetAwaiter().GetResult();

    public static async ValueTask<ModManifest> ReadAsync(string luaSource, string chunkName = "mod.lua")
    {
        if (luaSource.Length > MaxSourceChars)
        {
            throw new ManifestException($"{chunkName} is larger than {MaxSourceChars / 1024} KB");
        }

        // A fresh state with no standard library: the global table is empty and there is no way to reach a file, the clock or the OS.
        LuaState state = LuaState.Create();
        state.MaxCallDepth = 64;

        long instructions = 0;
        long startBytes = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        string? limitReason = null;
        using var cts = new CancellationTokenSource(MaxTime);

        var hook = new LuaFunction("limit", (context, ct) =>
        {
            instructions++;
            if (instructions > MaxInstructions)
            {
                limitReason = "it ran more than " + MaxInstructions + " instructions";
            }
            else if (GC.GetAllocatedBytesForCurrentThread() - startBytes > MaxAllocatedBytes)
            {
                limitReason = "it used more than " + (MaxAllocatedBytes / (1024 * 1024)) + " MB of memory";
            }
            else if (clock.Elapsed > MaxTime)
            {
                limitReason = "it ran longer than " + MaxTime.TotalSeconds + " s";
            }

            if (limitReason is not null)
            {
                throw new ManifestException(chunkName + " was stopped: " + limitReason);
            }

            return new ValueTask<int>(context.Return());
        });
        state.SetHook(hook, string.Empty, 1);

        LuaValue[] results;
        try
        {
            results = await state.DoStringAsync(luaSource, chunkName, cts.Token).ConfigureAwait(false);
        }
        catch (ManifestException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new ManifestException(chunkName + " was stopped: it ran longer than " + MaxTime.TotalSeconds + " s");
        }
        catch (Exception ex) when (ex is LuaParseException or LuaCompileException or LuaRuntimeException or LuaStackOverflowException
                                       or LuaAssertionException or InvalidOperationException or ArgumentException)
        {
            if (limitReason is not null)
            {
                throw new ManifestException(chunkName + " was stopped: " + limitReason);
            }

            throw new ManifestException(chunkName + ": " + FirstLine(ex.Message));
        }

        if (results.Length == 0 || results[0].Type != LuaValueType.Table)
        {
            throw new ManifestException(chunkName + " must return a table");
        }

        return Convert(results[0].Read<LuaTable>());
    }

    private static string FirstLine(string text)
    {
        int nl = text.IndexOf('\n', StringComparison.Ordinal);
        return nl < 0 ? text : text[..nl].TrimEnd();
    }

    private static ModManifest Convert(LuaTable t)
    {
        string id = Str(t, "id") ?? throw new ManifestException("mod.lua has no id");
        if (!ModIdRules.IsValid(id))
        {
            throw new ManifestException($"the id '{Clip(id, 40)}' is not valid (letters, digits, _ and - only)");
        }

        return new ModManifest
        {
            Id = id,
            Name = Clip(Str(t, "name") ?? string.Empty, 200),
            Version = Clip(Str(t, "version") ?? string.Empty, 64),
            Author = Clip(Str(t, "author") ?? string.Empty, 200),
            Description = Clip(Str(t, "description") ?? string.Empty, 4000),
            Priority = Num(t, "priority") ?? 0,
            DefaultEnabled = !(t["enabled"].Type == LuaValueType.Boolean && !t["enabled"].Read<bool>()),
            Requires = StringList(t["requires"]),
            Settings = Tables(t["settings"]).Select(ReadSetting).OfType<SettingDecl>().ToList(),
            Keys = Tables(t["keys"]).Select(ReadKey).OfType<KeyDecl>().ToList(),
        };
    }

    private static SettingDecl? ReadSetting(LuaTable s)
    {
        string? id = Str(s, "id");
        if (id is null)
        {
            return null;
        }

        string type = Str(s, "type") ?? "bool";
        LuaValue d = s["default"];
        string text = d.Type switch
        {
            LuaValueType.Boolean => d.Read<bool>() ? "on" : "off",
            LuaValueType.Number => FormatNumber(d.Read<double>(), type),
            LuaValueType.String => d.Read<string>(),
            _ => string.Empty,
        };
        return new SettingDecl(Clip(id, 64), Clip(type, 16), Clip(Str(s, "label") ?? id, 200), Clip(text, 100), Opt(Str(s, "section")), Opt(Str(s, "tooltip")));
    }

    private static string FormatNumber(double v, string type)
    {
        if (type == "color" && v >= 0 && v <= uint.MaxValue)
        {
            return "#" + ((uint)v).ToString("X8", CultureInfo.InvariantCulture);
        }

        return v.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static KeyDecl? ReadKey(LuaTable k)
    {
        string? id = Str(k, "id");
        return id is null ? null : new KeyDecl(Clip(id, 64), Clip(Str(k, "label") ?? id, 200), Clip(Str(k, "default") ?? string.Empty, 40));
    }

    private static string? Opt(string? s) => string.IsNullOrWhiteSpace(s) ? null : Clip(s, 500);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    private static string? Str(LuaTable t, string key)
    {
        LuaValue v = t[key];
        return v.Type switch
        {
            LuaValueType.String => v.Read<string>(),
            LuaValueType.Number => v.Read<double>().ToString("0.####", CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static double? Num(LuaTable t, string key) => t[key].Type == LuaValueType.Number ? t[key].Read<double>() : null;

    /// <summary>The entries of an array that are themselves tables (a malformed entry is skipped), capped.</summary>
    private static IEnumerable<LuaTable> Tables(LuaValue v)
    {
        if (v.Type != LuaValueType.Table)
        {
            yield break;
        }

        LuaTable t = v.Read<LuaTable>();
        int n = Math.Min(t.ArrayLength, MaxTableEntries);
        for (int i = 1; i <= n; i++)
        {
            if (t[i].Type == LuaValueType.Table)
            {
                yield return t[i].Read<LuaTable>();
            }
        }
    }

    private static List<string> StringList(LuaValue v)
    {
        var list = new List<string>();
        if (v.Type == LuaValueType.Table)
        {
            LuaTable t = v.Read<LuaTable>();
            int n = Math.Min(t.ArrayLength, 256);
            for (int i = 1; i <= n; i++)
            {
                if (t[i].Type == LuaValueType.String)
                {
                    list.Add(Clip(t[i].Read<string>(), 64));
                }
            }
        }

        return list;
    }
}
