using System.Text.Json;

namespace FortniteMatchCompiler;

public sealed class AppSettings
{
    public string SourceFolder { get; set; } = Path.Combine(
        Path.GetTempPath(), "Highlights", "Fortnite");

    public string OutputFolder { get; set; } = Path.Combine(GetVideosFolder(), "Fortnite Compilations");

    public decimal TargetMegabytes { get; set; } = 10.0m;
    public decimal KillBeforeSeconds { get; set; } = 5.5m;
    public decimal KillAfterSeconds { get; set; } = 1.5m;
    public decimal ResultBeforeSeconds { get; set; } = 6.0m;
    public decimal ResultAfterSeconds { get; set; } = 2.0m;

    public static string DataFolder
    {
        get
        {
            var overrideFolder = Environment.GetEnvironmentVariable("FORTNITE_COMPILER_DATA_DIR");
            return string.IsNullOrWhiteSpace(overrideFolder)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FortniteMatchCompiler")
                : Path.GetFullPath(overrideFolder);
        }
    }

    public static string SettingsPath => Path.Combine(DataFolder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

            // Versions before event-anchored trimming stored only the total excerpt
            // lengths. Preserve those totals while moving most of the time before
            // the event, where the useful action happens.
            using var document = JsonDocument.Parse(json);
            MigrateLegacyTiming(document.RootElement, settings);
            return settings;
        }
        catch (Exception exception)
        {
            AppLogger.Write($"Could not load settings; defaults will be used. {exception.Message}");
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(DataFolder);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var temporaryPath = Path.Combine(DataFolder, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string GetVideosFolder()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (!string.IsNullOrWhiteSpace(videos))
        {
            return videos;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Videos");
    }

    private static void MigrateLegacyTiming(JsonElement root, AppSettings settings)
    {
        if (!root.TryGetProperty(nameof(KillBeforeSeconds), out _) &&
            !root.TryGetProperty(nameof(KillAfterSeconds), out _) &&
            root.TryGetProperty("KillClipSeconds", out var legacyKill) &&
            legacyKill.TryGetDecimal(out var legacyKillTotal))
        {
            SplitLegacyTotal(
                legacyKillTotal,
                preferredAfterSeconds: 1.5m,
                out var before,
                out var after);
            settings.KillBeforeSeconds = before;
            settings.KillAfterSeconds = after;
        }

        if (!root.TryGetProperty(nameof(ResultBeforeSeconds), out _) &&
            !root.TryGetProperty(nameof(ResultAfterSeconds), out _) &&
            root.TryGetProperty("ResultClipSeconds", out var legacyResult) &&
            legacyResult.TryGetDecimal(out var legacyResultTotal))
        {
            SplitLegacyTotal(
                legacyResultTotal,
                preferredAfterSeconds: 2.0m,
                out var before,
                out var after);
            settings.ResultBeforeSeconds = before;
            settings.ResultAfterSeconds = after;
        }
    }

    private static void SplitLegacyTotal(
        decimal totalSeconds,
        decimal preferredAfterSeconds,
        out decimal beforeSeconds,
        out decimal afterSeconds)
    {
        totalSeconds = Math.Clamp(totalSeconds, 1.0m, 30.0m);
        afterSeconds = Math.Clamp(preferredAfterSeconds, 0.5m, totalSeconds - 0.5m);
        beforeSeconds = totalSeconds - afterSeconds;
    }
}
