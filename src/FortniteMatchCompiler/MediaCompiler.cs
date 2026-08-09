using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FortniteMatchCompiler;

public sealed class MediaCompiler
{
    private const int AudioBitrate = 64_000;
    private const int MinimumVideoBitrate = 120_000;
    private const double NvidiaPostEventSeconds = 4.5;
    private const double MinimumDurationToleranceSeconds = 0.5;
    private const double DurationToleranceFraction = 0.01;
    private readonly FfmpegTools _tools;

    public MediaCompiler(FfmpegTools tools)
    {
        _tools = tools;
    }

    public async Task<CompileResult> CompileAsync(
        HighlightMatch match,
        AppSettings settings,
        IProgress<CompileProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateSettings(settings);
        progress?.Report(new CompileProgress(0, "Checking source clips…"));

        Directory.CreateDirectory(settings.OutputFolder);
        EnsureFolderIsWritable(settings.OutputFolder);

        var prepared = new List<PreparedSegment>();
        foreach (var segment in match.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSourceIsReady(segment.Item);

            if (segment.Item.IsScreenshot)
            {
                prepared.Add(new PreparedSegment(
                    segment,
                    new MediaProbe(2.5, true, false),
                    0,
                    2.5));
                continue;
            }

            var probe = await ProbeAsync(segment.Item.FullPath, cancellationToken);
            if (!probe.HasVideo || probe.DurationSeconds <= 0)
            {
                throw new InvalidDataException($"This highlight is not a readable video:\n{segment.Item.FileName}");
            }

            var beforeSeconds = segment.Role == SegmentRole.Result
                ? (double)settings.ResultBeforeSeconds
                : (double)settings.KillBeforeSeconds +
                  Math.Max(0, segment.Item.KillCount - 1) * 4.0;
            var afterSeconds = segment.Role == SegmentRole.Result
                ? (double)settings.ResultAfterSeconds
                : (double)settings.KillAfterSeconds;
            var window = CalculateClipWindow(probe.DurationSeconds, beforeSeconds, afterSeconds);
            prepared.Add(new PreparedSegment(
                segment,
                probe,
                window.StartSeconds,
                window.DurationSeconds));
        }

        if (prepared.Count == 0)
        {
            throw new InvalidOperationException("No kill or result clips were selected for this match.");
        }

        var durationSeconds = prepared.Sum(segment => segment.DurationSeconds);
        var maximumBytes = checked((long)Math.Floor(settings.TargetMegabytes * 1_000_000m));
        var signature = BuildSignature(match, settings);
        var history = CompilationHistory.Load();
        var previous = history.Find(signature);
        if (previous is not null && File.Exists(previous.OutputPath))
        {
            try
            {
                var previousFile = new FileInfo(previous.OutputPath);
                if (previousFile.Length < maximumBytes)
                {
                    var previousProbe = await ProbeAsync(previous.OutputPath, cancellationToken);
                    if (previousProbe.HasVideo && previousProbe.HasAudio &&
                        IsDurationWithinTolerance(previousProbe.DurationSeconds, durationSeconds))
                    {
                        await ValidateDecodeAsync(previous.OutputPath, cancellationToken);
                        progress?.Report(new CompileProgress(100, "This match is already compiled."));
                        return new CompileResult
                        {
                            OutputPath = previous.OutputPath,
                            SizeBytes = previousFile.Length,
                            DurationSeconds = previousProbe.DurationSeconds,
                            UsedExistingFile = true
                        };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception cacheException)
            {
                AppLogger.Write(
                    $"Ignoring invalid cached compilation {previous.OutputPath}: {cacheException.Message}");
            }
        }

        var reserveBytes = Math.Max(300_000L, (long)Math.Ceiling(maximumBytes * 0.03));
        var encodingBudgetBytes = maximumBytes - reserveBytes;
        var videoBitrate = (long)Math.Floor((encodingBudgetBytes * 8.0 / durationSeconds) - AudioBitrate);
        if (videoBitrate < MinimumVideoBitrate)
        {
            throw new InvalidOperationException(
                "That size limit is too small for the selected number of clips. Increase the maximum size or shorten the clip lengths.");
        }

        // This is a maximum-size control, not a request to wastefully fill large allowances.
        videoBitrate = Math.Min(videoBitrate, 8_000_000);
        var finalPath = GetAvailableOutputPath(settings.OutputFolder, BuildOutputName(match));
        var operationId = Guid.NewGuid().ToString("N");
        var partialPath = Path.Combine(
            settings.OutputFolder,
            $".{Path.GetFileNameWithoutExtension(finalPath)}.partial-{operationId}.mp4");
        var temporaryFolder = Path.Combine(Path.GetTempPath(), "FortniteMatchCompiler", operationId);
        Directory.CreateDirectory(temporaryFolder);
        var outputHeight = SelectOutputHeight(videoBitrate);
        var outputWidth = MakeEven((int)Math.Round(outputHeight * 16.0 / 9.0));
        long encodedSize = 0;

        AppLogger.Write(
            $"Compiling {match.TerminalMarker.FileName}; segments={prepared.Count}; " +
            $"duration={durationSeconds:F3}s; max={maximumBytes}; initial-video-bitrate={videoBitrate}.");

        try
        {
            var manifestPath = await NormalizeSegmentsAsync(
                prepared,
                outputWidth,
                outputHeight,
                temporaryFolder,
                progress,
                cancellationToken);

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var passLogPrefix = Path.Combine(temporaryFolder, $"pass-{attempt}");

                progress?.Report(new CompileProgress(24, $"Encoding pass 1 of 2 (attempt {attempt})…"));
                var passOneArguments = BuildFfmpegArguments(
                    manifestPath,
                    videoBitrate,
                    passLogPrefix,
                    partialPath,
                    match,
                    firstPass: true);
                await RunFfmpegAsync(
                    passOneArguments,
                    durationSeconds,
                    24,
                    32,
                    "Encoding pass 1 of 2…",
                    progress,
                    cancellationToken);

                progress?.Report(new CompileProgress(57, $"Encoding pass 2 of 2 (attempt {attempt})…"));
                var passTwoArguments = BuildFfmpegArguments(
                    manifestPath,
                    videoBitrate,
                    passLogPrefix,
                    partialPath,
                    match,
                    firstPass: false);
                await RunFfmpegAsync(
                    passTwoArguments,
                    durationSeconds,
                    57,
                    35,
                    "Encoding pass 2 of 2…",
                    progress,
                    cancellationToken);

                encodedSize = new FileInfo(partialPath).Length;
                AppLogger.Write(
                    $"Encode attempt {attempt}: video-bitrate={videoBitrate}; bytes={encodedSize}.");

                if (encodedSize < maximumBytes)
                {
                    break;
                }

                if (attempt == 3)
                {
                    throw new InvalidOperationException(
                        $"The encoded video was still over {settings.TargetMegabytes:0.0} MB after three attempts.");
                }

                videoBitrate = Math.Max(
                    MinimumVideoBitrate,
                    (long)Math.Floor(videoBitrate * (maximumBytes * 0.95 / encodedSize)));
                progress?.Report(new CompileProgress(
                    24,
                    $"The first result was slightly large; retrying at {videoBitrate / 1000} kbps…"));
            }

            progress?.Report(new CompileProgress(94, "Validating the finished video…"));
            var outputProbe = await ProbeAsync(partialPath, cancellationToken);
            AppLogger.Write(
                $"Validation probe: duration={outputProbe.DurationSeconds:F3}s; " +
                $"expected={durationSeconds:F3}s; video={outputProbe.HasVideo}; audio={outputProbe.HasAudio}.");
            if (!outputProbe.HasVideo || !outputProbe.HasAudio ||
                !IsDurationWithinTolerance(outputProbe.DurationSeconds, durationSeconds))
            {
                throw new InvalidDataException("FFmpeg produced an incomplete or unexpectedly long output file.");
            }

            await ValidateDecodeAsync(partialPath, cancellationToken);
            encodedSize = new FileInfo(partialPath).Length;
            if (encodedSize >= maximumBytes)
            {
                throw new InvalidDataException("The validated video exceeded the requested maximum size.");
            }

            File.Move(partialPath, finalPath, overwrite: false);
            try
            {
                history.Add(signature, finalPath, encodedSize);
            }
            catch (Exception historyException)
            {
                // A valid video is already published; history is only an idempotence cache.
                AppLogger.Write($"Could not save compilation history: {historyException.Message}");
            }
            progress?.Report(new CompileProgress(100, "Compilation complete."));
            AppLogger.Write($"Compilation complete: {finalPath}; bytes={encodedSize}.");

            return new CompileResult
            {
                OutputPath = finalPath,
                SizeBytes = encodedSize,
                DurationSeconds = outputProbe.DurationSeconds,
                UsedExistingFile = false
            };
        }
        catch (OperationCanceledException)
        {
            AppLogger.Write("Compilation cancelled.");
            throw;
        }
        catch (Exception exception)
        {
            AppLogger.Write($"Compilation failed: {exception}");
            throw;
        }
        finally
        {
            TryDeleteFile(partialPath);
            TryDeleteDirectory(temporaryFolder);
        }
    }

    public async Task<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var arguments = new[]
        {
            "-v", "error",
            "-show_entries", "format=duration:stream=codec_type",
            "-of", "json",
            path
        };
        var json = await RunCaptureAsync(_tools.FfprobePath, arguments, cancellationToken);
        using var document = JsonDocument.Parse(json);

        var duration = 0.0;
        if (document.RootElement.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durationElement))
        {
            _ = double.TryParse(
                durationElement.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out duration);
        }

        var hasVideo = false;
        var hasAudio = false;
        if (document.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var codecType))
                {
                    continue;
                }

                var type = codecType.GetString();
                hasVideo |= string.Equals(type, "video", StringComparison.Ordinal);
                hasAudio |= string.Equals(type, "audio", StringComparison.Ordinal);
            }
        }

        return new MediaProbe(duration, hasVideo, hasAudio);
    }

    public static ClipWindow CalculateClipWindow(
        double sourceDurationSeconds,
        double beforeSeconds,
        double afterSeconds)
    {
        if (!double.IsFinite(sourceDurationSeconds) || sourceDurationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceDurationSeconds),
                "Source duration must be a positive number.");
        }

        if (!double.IsFinite(beforeSeconds) || beforeSeconds < 0 ||
            !double.IsFinite(afterSeconds) || afterSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(beforeSeconds),
                "Before and after timing must be non-negative numbers.");
        }

        var eventSeconds = Math.Max(0, sourceDurationSeconds - NvidiaPostEventSeconds);
        var durationSeconds = Math.Min(
            sourceDurationSeconds,
            beforeSeconds + afterSeconds);
        var latestStartSeconds = Math.Max(0, sourceDurationSeconds - durationSeconds);
        var startSeconds = Math.Clamp(
            eventSeconds - beforeSeconds,
            0,
            latestStartSeconds);

        return new ClipWindow(eventSeconds, startSeconds, durationSeconds);
    }

    internal static bool IsDurationWithinTolerance(
        double actualDurationSeconds,
        double expectedDurationSeconds)
    {
        if (!double.IsFinite(actualDurationSeconds) || actualDurationSeconds < 0 ||
            !double.IsFinite(expectedDurationSeconds) || expectedDurationSeconds <= 0)
        {
            return false;
        }

        var toleranceSeconds = Math.Max(
            MinimumDurationToleranceSeconds,
            expectedDurationSeconds * DurationToleranceFraction);
        return Math.Abs(actualDurationSeconds - expectedDurationSeconds) <= toleranceSeconds;
    }

    private static void ValidateSettings(AppSettings settings)
    {
        if (settings.TargetMegabytes is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.TargetMegabytes), "Maximum size must be between 1 and 500 MB.");
        }

        var timingValues = new[]
        {
            settings.KillBeforeSeconds,
            settings.KillAfterSeconds,
            settings.ResultBeforeSeconds,
            settings.ResultAfterSeconds
        };
        if (timingValues.Any(value => value is < 0.5m or > 30.0m) ||
            settings.KillBeforeSeconds + settings.KillAfterSeconds > 30.0m ||
            settings.ResultBeforeSeconds + settings.ResultAfterSeconds > 30.0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.KillBeforeSeconds),
                "Before and after timing must be between 0.5 and 30 seconds, with no excerpt longer than 30 seconds.");
        }
    }

    private static void EnsureSourceIsReady(HighlightItem item)
    {
        if (!File.Exists(item.FullPath))
        {
            throw new FileNotFoundException("A selected NVIDIA highlight no longer exists.", item.FullPath);
        }

        var file = new FileInfo(item.FullPath);
        if (DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromSeconds(5))
        {
            throw new IOException(
                $"NVIDIA is still saving {item.FileName}. Wait a few seconds and press Refresh.");
        }

        using var stream = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
        {
            throw new InvalidDataException($"The source file is empty: {item.FileName}");
        }
    }

    private static void EnsureFolderIsWritable(string folder)
    {
        var testPath = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(testPath))
            {
            }
        }
        finally
        {
            TryDeleteFile(testPath);
        }
    }

    private static string BuildSignature(HighlightMatch match, AppSettings settings)
    {
        var builder = new StringBuilder();
        builder.Append("v3|pipeline=ffv1-concat-v1|event-tail=")
            .Append(NvidiaPostEventSeconds.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(settings.TargetMegabytes.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(settings.KillBeforeSeconds.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(settings.KillAfterSeconds.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(settings.ResultBeforeSeconds.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(settings.ResultAfterSeconds.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(Path.GetFullPath(settings.OutputFolder).TrimEnd(Path.DirectorySeparatorChar))
            .Append('|');

        foreach (var segment in match.Segments)
        {
            builder.Append(segment.Role).Append('|')
                .Append(segment.Item.FullPath).Append('|')
                .Append(segment.Item.Length).Append('|')
                .Append(segment.Item.LastWriteTimeUtc.Ticks).Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string BuildOutputName(HighlightMatch match)
    {
        var name = $"Fortnite {match.StartTime:yyyy-MM-dd HH.mm}-{match.EndTime:HH.mm} - " +
                   $"{match.KillCount} kill{(match.KillCount == 1 ? string.Empty : "s")} - " +
                   $"{match.ResultDisplay}.mp4";
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }

    private static string GetAvailableOutputPath(string outputFolder, string fileName)
    {
        var candidate = Path.Combine(outputFolder, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var number = 2; number < 1000; number++)
        {
            candidate = Path.Combine(outputFolder, $"{stem} ({number}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Could not choose a unique output filename.");
    }

    private static int SelectOutputHeight(long videoBitrate)
    {
        return videoBitrate switch
        {
            < 350_000 => 360,
            < 700_000 => 480,
            < 1_500_000 => 540,
            _ => 720
        };
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;

    private async Task<string> NormalizeSegmentsAsync(
        IReadOnlyList<PreparedSegment> segments,
        int width,
        int height,
        string temporaryFolder,
        IProgress<CompileProgress>? progress,
        CancellationToken cancellationToken)
    {
        var manifestLines = new List<string> { "ffconcat version 1.0" };
        for (var index = 0; index < segments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = segments[index];
            var intermediatePath = Path.Combine(temporaryFolder, $"segment-{index:D3}.mkv");
            var percentStart = 2 + (int)Math.Floor(index * 21.0 / segments.Count);
            var percentEnd = 2 + (int)Math.Floor((index + 1) * 21.0 / segments.Count);
            var message = $"Preparing clip {index + 1} of {segments.Count}…";
            progress?.Report(new CompileProgress(percentStart, message));

            await RunFfmpegAsync(
                BuildNormalizationArguments(segment, width, height, intermediatePath),
                segment.DurationSeconds,
                percentStart,
                Math.Max(1, percentEnd - percentStart),
                message,
                progress,
                cancellationToken);

            if (!File.Exists(intermediatePath) || new FileInfo(intermediatePath).Length == 0)
            {
                throw new InvalidDataException(
                    $"FFmpeg could not prepare this highlight:\n{segment.Segment.Item.FileName}");
            }

            manifestLines.Add($"file '{EscapeConcatPath(intermediatePath)}'");
            manifestLines.Add($"duration {FormatSeconds(segment.DurationSeconds)}");
        }

        var manifestPath = Path.Combine(temporaryFolder, "segments.ffconcat");
        await File.WriteAllLinesAsync(
            manifestPath,
            manifestLines,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        progress?.Report(new CompileProgress(23, "Clips prepared."));
        return manifestPath;
    }

    private static IReadOnlyList<string> BuildNormalizationArguments(
        PreparedSegment segment,
        int width,
        int height,
        string outputPath)
    {
        var start = FormatSeconds(segment.StartSeconds);
        var duration = FormatSeconds(segment.DurationSeconds);
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-progress", "pipe:1", "-nostats"
        };

        if (segment.Segment.Item.IsScreenshot)
        {
            arguments.AddRange(new[]
            {
                "-loop", "1",
                "-framerate", "30",
                "-i", segment.Segment.Item.FullPath
            });
        }
        else
        {
            arguments.AddRange(new[]
            {
                "-ss", start,
                "-i", segment.Segment.Item.FullPath
            });

            // This older FFmpeg build can stop a shared decoder early when separate
            // video and audio filter branches consume it. Independent inputs keep
            // every normalized excerpt exact and are still reading the same file.
            if (segment.Probe.HasAudio)
            {
                arguments.AddRange(new[]
                {
                    "-ss", start,
                    "-i", segment.Segment.Item.FullPath
                });
            }
        }

        var videoFilter =
            "[0:v]setpts=PTS-STARTPTS," +
            $"scale={width}:{height}:force_original_aspect_ratio=decrease," +
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2," +
            "fps=30,tpad=stop_mode=clone:stop_duration=30s," +
            "setpts=N/(30*TB),format=yuv420p,setsar=1[vout]";

        string audioFilter;
        if (segment.Probe.HasAudio && !segment.Segment.Item.IsScreenshot)
        {
            var fadeOutStart = Math.Max(0, segment.DurationSeconds - 0.02);
            audioFilter =
                "[1:a]asetpts=PTS-STARTPTS," +
                "aresample=48000:async=1:first_pts=0," +
                "aformat=sample_fmts=s16:sample_rates=48000:channel_layouts=stereo," +
                $"afade=t=in:st=0:d=0.02,afade=t=out:st={FormatSeconds(fadeOutStart)}:d=0.02," +
                "apad,asetpts=N/SR/TB[aout]";
        }
        else
        {
            audioFilter =
                "anullsrc=r=48000:cl=stereo," +
                "asetpts=N/SR/TB[aout]";
        }

        arguments.AddRange(new[]
        {
            "-filter_complex", $"{videoFilter};{audioFilter}",
            "-map", "[vout]",
            "-map", "[aout]",
            "-c:v", "ffv1",
            "-level", "3",
            "-g", "1",
            "-c:a", "pcm_s16le",
            "-ar", "48000",
            "-ac", "2",
            "-map_metadata", "-1",
            "-t", duration,
            outputPath
        });
        return arguments;
    }

    private static string EscapeConcatPath(string path) =>
        Path.GetFullPath(path)
            .Replace('\\', '/')
            .Replace("'", "'\\''", StringComparison.Ordinal);

    private static IReadOnlyList<string> BuildFfmpegArguments(
        string manifestPath,
        long videoBitrate,
        string passLogPrefix,
        string outputPath,
        HighlightMatch match,
        bool firstPass)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-progress", "pipe:1", "-nostats",
            "-f", "concat",
            "-safe", "0",
            "-i", manifestPath,
            "-map", "0:v:0"
        };

        if (!firstPass)
        {
            arguments.AddRange(new[] { "-map", "0:a:0" });
        }

        arguments.AddRange(new[]
        {
            "-c:v", "libx264",
            "-preset", "medium",
            "-profile:v", "high",
            "-level:v", "3.1",
            "-b:v", videoBitrate.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
            "-pass", firstPass ? "1" : "2",
            "-passlogfile", passLogPrefix
        });

        if (firstPass)
        {
            arguments.AddRange(new[] { "-an", "-f", "null", "NUL" });
        }
        else
        {
            arguments.AddRange(new[]
            {
                "-c:a", "aac",
                "-b:a", "64k",
                "-ar", "48000",
                "-ac", "2",
                "-map_metadata", "-1",
                "-metadata", $"title=Fortnite match - {match.KillCount} kills - {match.ResultDisplay}",
                "-metadata", "comment=Compiled from NVIDIA Fortnite highlights",
                "-movflags", "+faststart",
                outputPath
            });
        }

        return arguments;
    }

    private async Task RunFfmpegAsync(
        IReadOnlyList<string> arguments,
        double totalDuration,
        int percentOffset,
        int percentSpan,
        string message,
        IProgress<CompileProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _tools.FfmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start FFmpeg.");
        }

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The process may have exited between the checks.
            }
        });

        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!line.StartsWith("out_time=", StringComparison.Ordinal) ||
                    !TimeSpan.TryParse(line[9..], CultureInfo.InvariantCulture, out var elapsed))
                {
                    continue;
                }

                var fraction = totalDuration <= 0
                    ? 0
                    : Math.Clamp(elapsed.TotalSeconds / totalDuration, 0, 1);
                progress?.Report(new CompileProgress(
                    percentOffset + (int)Math.Round(fraction * percentSpan),
                    message));
            }

            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);

            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg could not create the compilation.\n\n{Tail(error, 3500)}");
        }
    }

    private async Task ValidateDecodeAsync(string path, CancellationToken cancellationToken)
    {
        var arguments = new[]
        {
            "-hide_banner", "-loglevel", "error", "-xerror", "-nostdin",
            "-i", path,
            "-f", "null", "NUL"
        };
        _ = await RunCaptureAsync(_tools.FfmpegPath, arguments, cancellationToken);
    }

    private static async Task<string> RunCaptureAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        });

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);

            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} failed.\n\n{Tail(error, 3500)}");
        }

        return output;
    }

    private static string FormatSeconds(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Tail(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[^maximumCharacters..];

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process exited or is already being terminated.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Write($"Could not delete temporary file {path}: {exception.Message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Write($"Could not delete temporary folder {path}: {exception.Message}");
        }
    }
}
