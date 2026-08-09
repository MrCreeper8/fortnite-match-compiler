namespace FortniteMatchCompiler;

public sealed record FfmpegTools(string FfmpegPath, string FfprobePath);

public static class FfmpegLocator
{
    public static FfmpegTools Locate()
    {
        var explicitFfmpeg = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        var ffmpegCandidates = new List<string?>
        {
            explicitFfmpeg,
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            @"C:\Program Files\ffmpeg\ffmpeg\bin\ffmpeg.exe",
            FindOnPath("ffmpeg.exe")
        };

        var ffmpeg = ffmpegCandidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .FirstOrDefault(File.Exists);

        if (ffmpeg is null)
        {
            throw new FileNotFoundException(
                "FFmpeg was not found. Install FFmpeg or place ffmpeg.exe and ffprobe.exe beside this app.");
        }

        var ffprobeCandidates = new[]
        {
            Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffprobe.exe"),
            FindOnPath("ffprobe.exe")
        };

        var ffprobe = ffprobeCandidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .FirstOrDefault(File.Exists);

        if (ffprobe is null)
        {
            throw new FileNotFoundException(
                "ffprobe.exe was not found. It normally comes in the same folder as ffmpeg.exe.");
        }

        return new FfmpegTools(ffmpeg, ffprobe);
    }

    private static string? FindOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }
}
