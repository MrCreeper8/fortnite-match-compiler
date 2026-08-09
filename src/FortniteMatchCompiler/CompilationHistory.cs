using System.Text.Json;

namespace FortniteMatchCompiler;

public sealed class CompilationHistoryEntry
{
    public string Signature { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class CompilationHistory
{
    public List<CompilationHistoryEntry> Entries { get; set; } = new();

    private static string HistoryPath => Path.Combine(AppSettings.DataFolder, "history.json");

    public static CompilationHistory Load()
    {
        try
        {
            if (!File.Exists(HistoryPath))
            {
                return new CompilationHistory();
            }

            return JsonSerializer.Deserialize<CompilationHistory>(File.ReadAllText(HistoryPath)) ??
                   new CompilationHistory();
        }
        catch (Exception exception)
        {
            AppLogger.Write($"Could not load compilation history. {exception.Message}");
            return new CompilationHistory();
        }
    }

    public CompilationHistoryEntry? Find(string signature)
    {
        return Entries.LastOrDefault(entry =>
            string.Equals(entry.Signature, signature, StringComparison.Ordinal));
    }

    public void Add(string signature, string outputPath, long sizeBytes)
    {
        Entries.RemoveAll(entry => string.Equals(entry.Signature, signature, StringComparison.Ordinal));
        Entries.Add(new CompilationHistoryEntry
        {
            Signature = signature,
            OutputPath = outputPath,
            SizeBytes = sizeBytes,
            CreatedUtc = DateTime.UtcNow
        });

        if (Entries.Count > 100)
        {
            Entries = Entries.OrderByDescending(entry => entry.CreatedUtc).Take(100).ToList();
        }

        Directory.CreateDirectory(AppSettings.DataFolder);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var temporaryPath = Path.Combine(
            AppSettings.DataFolder,
            $".history-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, HistoryPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
