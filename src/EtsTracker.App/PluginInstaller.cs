using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace EtsTracker.App;

public enum PluginState
{
    GameNotFound,   // папка ETS2 не найдена
    NotInstalled,   // игра есть, плагина нет
    Outdated,       // в игре лежит другой scs-telemetry.dll
    Installed,      // стоит тот же плагин, что встроен в трекер
}

/// <summary>
/// Ставит встроенный плагин телеметрии (scs-telemetry.dll) в папку игры:
/// Euro Truck Simulator 2\bin\win_x64\plugins. Игру ищет в библиотеках Steam.
/// </summary>
public static class PluginInstaller
{
    private const string FileName = "scs-telemetry.dll";
    private const string GameFolder = "Euro Truck Simulator 2";
    private static readonly string GameExe = Path.Combine("bin", "win_x64", "eurotrucks2.exe");

    /// <summary>Папка игры, указанная вручную (если игра не в Steam). Хранится в data/app.json.</summary>
    public static string? FindGame(string? manualDir)
    {
        if (IsGameDir(manualDir)) return manualDir;
        return SteamLibraries()
            .Select(lib => Path.Combine(lib, "steamapps", "common", GameFolder))
            .FirstOrDefault(IsGameDir);
    }

    public static bool IsGameDir(string? dir)
        => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, GameExe));

    public static string PluginPath(string gameDir)
        => Path.Combine(gameDir, "bin", "win_x64", "plugins", FileName);

    public static PluginState GetState(string? gameDir)
    {
        if (gameDir == null) return PluginState.GameNotFound;
        var path = PluginPath(gameDir);
        if (!File.Exists(path)) return PluginState.NotInstalled;
        try
        {
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(Embedded()) ? PluginState.Installed : PluginState.Outdated;
        }
        catch (IOException)
        {
            return PluginState.Installed; // файл занят запущенной игрой — значит, плагин на месте
        }
    }

    /// <summary>Копирует плагин в игру. Бросает понятное исключение, если игра запущена или нет прав.</summary>
    public static void Install(string gameDir)
    {
        var path = PluginPath(gameDir);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Embedded());
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Нет прав на запись в папку игры. Запустите трекер от имени администратора " +
                $"или скопируйте плагин вручную в {Path.GetDirectoryName(path)}.");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("Не удалось записать плагин — закройте игру и попробуйте ещё раз.");
        }
    }

    private static byte[]? _embedded;

    private static byte[] Embedded()
    {
        if (_embedded != null) return _embedded;
        using var stream = typeof(PluginInstaller).Assembly.GetManifestResourceStream(FileName)
            ?? throw new InvalidOperationException("Плагин не встроен в сборку.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return _embedded = ms.ToArray();
    }

    /// <summary>Папки библиотек Steam: основная (из реестра) и дополнительные (из libraryfolders.vdf).</summary>
    private static IEnumerable<string> SteamLibraries()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                    ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (string.IsNullOrEmpty(steam)) yield break;

        steam = Path.GetFullPath(steam);
        yield return steam;

        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        string text;
        try { text = File.ReadAllText(vdf); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }
}
