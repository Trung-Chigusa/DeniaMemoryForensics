using System.IO;
using System.Text.Json;
using DeniaMemoryForensics.Models;

namespace DeniaMemoryForensics.Services;

public static class AppPaths
{
    public static string BaseDirectory => AppContext.BaseDirectory;

    public static string SettingsPath => Path.Combine(BaseDirectory, "denia-settings.json");

    public static string DefaultOutputRoot
    {
        get
        {
            var path = Path.Combine(BaseDirectory, "denia_out");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static DeniaSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<DeniaSettings>(json);
                if (settings is not null)
                {
                    if (string.IsNullOrWhiteSpace(settings.OutputRoot))
                    {
                        settings.OutputRoot = DefaultOutputRoot;
                    }

                    return settings;
                }
            }
        }
        catch
        {
            // The UI continues with defaults if settings are unreadable.
        }

        return new DeniaSettings
        {
            VolatilityEnginePath = DetectVolatilityEngine(),
            OutputRoot = DefaultOutputRoot
        };
    }

    public static void SaveSettings(DeniaSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.OutputRoot))
        {
            settings.OutputRoot = DefaultOutputRoot;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    public static string DetectVolatilityEngine()
    {
        var env = Environment.GetEnvironmentVariable("DENIA_VOL_ENGINE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var candidates = new List<string>
        {
            Path.Combine(BaseDirectory, "Volatility3Analyzer.exe"),
            Path.Combine(BaseDirectory, "volatility3-2.26.2", "dist", "Volatility3Analyzer.exe")
        };

        var cursor = new DirectoryInfo(BaseDirectory);
        for (var i = 0; i < 8 && cursor is not null; i++, cursor = cursor.Parent)
        {
            candidates.Add(Path.Combine(cursor.FullName, "volatility3-2.26.2", "dist", "Volatility3Analyzer.exe"));
            candidates.Add(Path.Combine(cursor.FullName, "volatility3-2.26.2", "volatility3-2.26.2", "dist", "Volatility3Analyzer.exe"));
        }

        return candidates.FirstOrDefault(File.Exists) ?? "";
    }
}
