namespace FortniteMatchCompiler;

public static class AppLogger
{
    private static readonly object Gate = new();
    public static string LogPath => Path.Combine(AppSettings.DataFolder, "compiler.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.DataFolder);
                File.AppendAllText(
                    LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never stop a compilation.
        }
    }
}
