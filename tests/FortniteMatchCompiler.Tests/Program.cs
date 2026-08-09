using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FortniteMatchCompiler;

namespace FortniteMatchCompiler.Tests;

public static class Program
{
    private static int _passed;

    public static async Task<int> Main(string[] args)
    {
        var previousDataFolder = Environment.GetEnvironmentVariable(
            "FORTNITE_COMPILER_DATA_DIR");
        using var sandbox = new SyntheticSandbox();
        Environment.SetEnvironmentVariable(
            "FORTNITE_COMPILER_DATA_DIR",
            sandbox.CreateDirectory("app-data"));

        try
        {
            Run("event classification", () => TestClassification(sandbox));
            Run("completed match grouping, knocks, and multi-kills", () =>
                TestCompletedMatchGrouping(sandbox));
            Run("victory screenshot grace and nearby terminal media", () =>
                TestScreenshotTerminalHandling(sandbox));
            Run("newer unfinished match detection", () =>
                TestNewerUnfinishedMatch(sandbox));
            Run("cross-midnight grouping", () => TestCrossMidnight(sandbox));
            Run("90-minute quarantine", () => TestSessionQuarantine(sandbox));
            Run("legacy timing migration", () => TestLegacyTimingMigration(sandbox));
            Run("event-anchored excerpt timing", TestClipWindows);
            Run("duration validation tolerance", TestDurationTolerance);
            await RunAsync("single-instance command pipe", TestSingleInstancePipeAsync);
            await RunAsync(
                "single-instance shutdown without UI context pump",
                TestSingleInstanceShutdownWithoutContextPumpAsync);
            if (args.Contains("--ffmpeg-integration", StringComparer.OrdinalIgnoreCase))
            {
                await RunAsync("two-segment FFmpeg compilation", () =>
                    TestFfmpegIntegrationAsync(sandbox));
            }

            Console.WriteLine($"PASS: {_passed} hermetic tests completed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception.Message}");
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "FORTNITE_COMPILER_DATA_DIR",
                previousDataFolder);
        }
    }

    private static void TestClassification(SyntheticSandbox sandbox)
    {
        Equal(HighlightKind.Kill, HighlightScanner.Classify("Eliminierung"),
            "A German elimination event should be classified as a kill.");
        Equal(HighlightKind.Kill, HighlightScanner.Classify("Triple Elimination"),
            "An English elimination event should be classified as a kill.");
        Equal(HighlightKind.Eliminated, HighlightScanner.Classify("Eliminiert"),
            "The terminal loss label must not be classified as a kill.");
        Equal(HighlightKind.Victory, HighlightScanner.Classify("Victory Royale"),
            "Victory Royale should be classified as a victory.");
        Equal(HighlightKind.Knock, HighlightScanner.Classify("Am Boden"),
            "Am Boden should be classified as a knock.");
        Equal(HighlightKind.Other, HighlightScanner.Classify("Neue Sturmphase"),
            "Unrelated events should remain unclassified.");

        var folder = sandbox.CreateDirectory("classification");
        var eventTime = new DateTime(2031, 2, 3, 10, 15, 0);
        var doublePath = sandbox.CreateVideo(
            folder,
            eventTime,
            sequence: 1,
            label: "Doppeleliminierung");
        var triplePath = sandbox.CreateVideo(
            folder,
            eventTime.AddSeconds(1),
            sequence: 2,
            label: "Dreifacheliminerung");
        var quadPath = sandbox.CreateVideo(
            folder,
            eventTime.AddSeconds(2),
            sequence: 3,
            label: "Quad Elimination");

        Equal(2, Parse(doublePath).KillCount, "A double-kill clip should credit two kills.");
        Equal(3, Parse(triplePath).KillCount, "A triple-kill clip should credit three kills.");
        Equal(4, Parse(quadPath).KillCount, "A quad-kill clip should credit four kills.");
    }

    private static void TestCompletedMatchGrouping(SyntheticSandbox sandbox)
    {
        var folder = sandbox.CreateDirectory("completed-grouping");
        var firstStart = new DateTime(2031, 3, 4, 12, 0, 0);
        sandbox.CreateVideo(folder, firstStart, 1, "Eliminierung");
        sandbox.CreateVideo(folder, firstStart.AddSeconds(5), 2, "Am Boden");
        sandbox.CreateVideo(folder, firstStart.AddSeconds(10), 3, "Dreifacheliminerung");
        sandbox.CreateVideo(folder, firstStart.AddSeconds(20), 4, "Eliminiert");

        var secondStart = firstStart.AddMinutes(15);
        sandbox.CreateVideo(folder, secondStart, 5, "Eliminierung");
        sandbox.CreateVideo(folder, secondStart.AddSeconds(12), 6, "Sieg");

        var scan = new HighlightScanner().Scan(folder);
        Equal(2, scan.CompletedMatches.Count,
            "Two terminal markers should produce two completed matches.");
        Equal(0, scan.UnfinishedItems.Count,
            "Both synthetic matches have terminal markers.");

        var first = scan.CompletedMatches[0];
        Equal(MatchResult.Eliminated, first.Result,
            "The first match should end in elimination.");
        Equal(4, first.KillCount,
            "One single-kill and one triple-kill clip should credit four kills.");
        Equal(2, first.KillClipCount,
            "A multi-kill event remains one source clip.");
        Equal(3, first.Segments.Count,
            "Only two kill clips and the terminal result should be selected.");
        Expect(first.AllItems.Any(item => item.Kind == HighlightKind.Knock),
            "The knock should remain available in the match metadata.");
        Expect(first.Segments.All(segment => segment.Item.Kind != HighlightKind.Knock),
            "Knock clips must not be selected for compilation.");
        Equal(SegmentRole.Result, first.Segments[^1].Role,
            "The terminal clip should be the final compilation segment.");

        var second = scan.CompletedMatches[1];
        Equal(MatchResult.Victory, second.Result,
            "The second match should end in victory.");
        Equal(1, second.KillCount,
            "The second match should retain its own kill count.");
        Equal(2, second.Segments.Count,
            "The second match should not absorb segments from the first.");
    }

    private static void TestScreenshotTerminalHandling(SyntheticSandbox sandbox)
    {
        var scanner = new HighlightScanner();
        var youngFolder = sandbox.CreateDirectory("young-screenshot");
        var screenshot = sandbox.CreateScreenshot(
            youngFolder,
            new DateTime(2031, 4, 5, 9, 0, 0),
            sequence: 1,
            label: "Sieg");

        var provisional = scanner.Scan(youngFolder);
        Equal(0, provisional.CompletedMatches.Count,
            "A newly written victory screenshot should remain provisional.");
        Equal(1, provisional.UnfinishedItems.Count,
            "The provisional screenshot should be exposed as unfinished media.");

        File.SetLastWriteTimeUtc(screenshot, DateTime.UtcNow - TimeSpan.FromSeconds(20));
        var aged = scanner.Scan(youngFolder);
        Equal(1, aged.CompletedMatches.Count,
            "A victory screenshot older than the grace period should close the match.");
        Expect(aged.LatestCompleted!.Segments.Single().Item.IsScreenshot,
            "A screenshot-only victory should use the screenshot as result media.");

        var nearbyFolder = sandbox.CreateDirectory("nearby-terminal");
        var baseTime = new DateTime(2031, 4, 5, 14, 0, 0);
        var earlierKill = sandbox.CreateVideo(
            nearbyFolder,
            baseTime,
            sequence: 2,
            label: "Eliminierung");
        var victoryScreenshot = sandbox.CreateScreenshot(
            nearbyFolder,
            baseTime.AddSeconds(4),
            sequence: 3,
            label: "Sieg");
        var nearestVideo = sandbox.CreateVideo(
            nearbyFolder,
            baseTime.AddSeconds(6),
            sequence: 4,
            label: "Eliminierung");
        File.SetLastWriteTimeUtc(
            victoryScreenshot,
            DateTime.UtcNow - TimeSpan.FromSeconds(20));

        var nearby = scanner.Scan(nearbyFolder).LatestCompleted!;
        Equal(MatchResult.Victory, nearby.Result,
            "The screenshot should still define the match result.");
        Equal(2, nearby.Segments.Count,
            "Nearby terminal media should be moved, not duplicated.");
        Equal(Path.GetFullPath(earlierKill), nearby.Segments[0].Item.FullPath,
            "The earlier kill should remain a normal kill segment.");
        Equal(Path.GetFullPath(nearestVideo), nearby.Segments[^1].Item.FullPath,
            "The closest video should provide the terminal result media.");
        Equal(SegmentRole.Result, nearby.Segments[^1].Role,
            "Nearby terminal media should be forced to the result role.");
    }

    private static void TestNewerUnfinishedMatch(SyntheticSandbox sandbox)
    {
        var folder = sandbox.CreateDirectory("newer-unfinished");
        var start = new DateTime(2031, 5, 6, 16, 0, 0);
        sandbox.CreateVideo(folder, start, 1, "Eliminierung");
        sandbox.CreateVideo(folder, start.AddSeconds(15), 2, "Eliminiert");
        var unfinishedPath = sandbox.CreateVideo(
            folder,
            start.AddMinutes(25),
            sequence: 3,
            label: "Eliminierung");

        var scan = new HighlightScanner().Scan(folder);
        Equal(1, scan.CompletedMatches.Count,
            "The earlier match should remain completed.");
        Equal(1, scan.UnfinishedItems.Count,
            "The later unterminated match should remain unfinished.");
        Equal(Path.GetFullPath(unfinishedPath), scan.UnfinishedItems[0].FullPath,
            "The later kill should be the unfinished item.");
        Expect(scan.HasNewerUnfinishedMatch,
            "A kill newer than the latest terminal marker should block latest-match compilation.");

        var screenshotFolder = sandbox.CreateDirectory("newer-victory-screenshot");
        sandbox.CreateVideo(screenshotFolder, start, 1, "Eliminierung");
        sandbox.CreateVideo(screenshotFolder, start.AddSeconds(15), 2, "Eliminiert");
        sandbox.CreateScreenshot(
            screenshotFolder,
            start.AddMinutes(25),
            sequence: 3,
            label: "Sieg");
        var provisionalVictory = new HighlightScanner().Scan(screenshotFolder);
        Expect(provisionalVictory.HasNewerUnfinishedMatch,
            "A fresh victory screenshot should block compiling the previous completed match while its terminal footage is still pending.");
    }

    private static void TestCrossMidnight(SyntheticSandbox sandbox)
    {
        var folder = sandbox.CreateDirectory("cross-midnight");
        var beforeMidnight = new DateTime(2031, 6, 7, 23, 59, 56);
        sandbox.CreateVideo(folder, beforeMidnight, 1, "Eliminierung");
        sandbox.CreateVideo(folder, beforeMidnight.AddSeconds(9), 2, "Eliminiert");

        var scan = new HighlightScanner().Scan(folder);
        Equal(1, scan.CompletedMatches.Count,
            "Highlights on adjacent dates should remain one match when only seconds apart.");
        Equal(1, scan.LatestCompleted!.KillCount,
            "The pre-midnight kill should be credited to the post-midnight terminal.");
        Equal(2, scan.LatestCompleted.Segments.Count,
            "The cross-midnight match should include its kill and terminal clips.");
    }

    private static void TestSessionQuarantine(SyntheticSandbox sandbox)
    {
        var folder = sandbox.CreateDirectory("session-quarantine");
        var abandonedTime = new DateTime(2031, 7, 8, 8, 0, 0);
        var abandonedPath = sandbox.CreateVideo(
            folder,
            abandonedTime,
            sequence: 1,
            label: "Eliminierung");
        sandbox.CreateVideo(
            folder,
            abandonedTime.AddMinutes(90).AddSeconds(1),
            sequence: 2,
            label: "Eliminiert");

        var scan = new HighlightScanner().Scan(folder);
        Equal(1, scan.CompletedMatches.Count,
            "The later terminal marker should still form a completed result.");
        Equal(0, scan.LatestCompleted!.KillCount,
            "A kill separated by more than 90 minutes must not be merged into the result.");
        Equal(1, scan.UnfinishedItems.Count,
            "The abandoned pre-gap kill should be quarantined as unfinished.");
        Equal(Path.GetFullPath(abandonedPath), scan.UnfinishedItems[0].FullPath,
            "The quarantined item should be the pre-gap kill.");
    }

    private static void TestLegacyTimingMigration(SyntheticSandbox sandbox)
    {
        var sourceFolder = sandbox.CreateDirectory("legacy-source");
        var outputFolder = sandbox.CreateDirectory("legacy-output");
        var legacy = new Dictionary<string, object>
        {
            [nameof(AppSettings.SourceFolder)] = sourceFolder,
            [nameof(AppSettings.OutputFolder)] = outputFolder,
            [nameof(AppSettings.TargetMegabytes)] = 12.5m,
            ["KillClipSeconds"] = 7.0m,
            ["ResultClipSeconds"] = 8.0m
        };

        File.WriteAllText(
            AppSettings.SettingsPath,
            JsonSerializer.Serialize(legacy, new JsonSerializerOptions { WriteIndented = true }));

        var settings = AppSettings.Load();
        Equal(Path.GetFullPath(sourceFolder), Path.GetFullPath(settings.SourceFolder),
            "Migration should retain the configured source folder.");
        Equal(Path.GetFullPath(outputFolder), Path.GetFullPath(settings.OutputFolder),
            "Migration should retain the configured output folder.");
        Equal(5.5m, settings.KillBeforeSeconds,
            "A legacy seven-second kill excerpt should migrate to 5.5 seconds before.");
        Equal(1.5m, settings.KillAfterSeconds,
            "A legacy seven-second kill excerpt should migrate to 1.5 seconds after.");
        Equal(6.0m, settings.ResultBeforeSeconds,
            "A legacy eight-second result excerpt should migrate to six seconds before.");
        Equal(2.0m, settings.ResultAfterSeconds,
            "A legacy eight-second result excerpt should migrate to two seconds after.");
    }

    private static void TestClipWindows()
    {
        var kill = MediaCompiler.CalculateClipWindow(20.203, 5.5, 1.5);
        Near(15.703, kill.EventSeconds, 0.001,
            "The event should be anchored 4.5 seconds before the source ends.");
        Near(10.203, kill.StartSeconds, 0.001,
            "The standard kill excerpt should retain 5.5 seconds before the event.");
        Near(7.0, kill.DurationSeconds, 0.001,
            "The standard kill excerpt should last seven seconds.");

        var result = MediaCompiler.CalculateClipWindow(15.449, 6.0, 2.0);
        Near(4.949, result.StartSeconds, 0.001,
            "The result excerpt should retain six seconds before the event.");
        Near(8.0, result.DurationSeconds, 0.001,
            "The result excerpt should last eight seconds.");

        var tripleKill = MediaCompiler.CalculateClipWindow(20.203, 13.5, 1.5);
        Near(2.203, tripleKill.StartSeconds, 0.001,
            "Extra multi-kill time should extend the excerpt before the event.");
        Near(15.0, tripleKill.DurationSeconds, 0.001,
            "The extended triple-kill excerpt should last fifteen seconds.");

        var shortSource = MediaCompiler.CalculateClipWindow(5.0, 5.5, 1.5);
        Near(0, shortSource.StartSeconds, 0.001,
            "A short source should begin at its first frame.");
        Near(5.0, shortSource.DurationSeconds, 0.001,
            "A short source should be retained in full.");
    }

    private static void TestDurationTolerance()
    {
        Expect(MediaCompiler.IsDurationWithinTolerance(99.01, 100.0),
            "A duration within one percent should be accepted.");
        Expect(MediaCompiler.IsDurationWithinTolerance(100.99, 100.0),
            "The one-percent tolerance should be symmetric.");
        Expect(!MediaCompiler.IsDurationWithinTolerance(98.9, 100.0),
            "A duration more than one percent short should be rejected.");
        Expect(!MediaCompiler.IsDurationWithinTolerance(101.1, 100.0),
            "A duration more than one percent long should be rejected.");

        Expect(MediaCompiler.IsDurationWithinTolerance(3.51, 4.0),
            "Short outputs should use the half-second minimum tolerance.");
        Expect(MediaCompiler.IsDurationWithinTolerance(4.49, 4.0),
            "The half-second minimum tolerance should be symmetric.");
        Expect(!MediaCompiler.IsDurationWithinTolerance(3.4, 4.0),
            "A short output outside the half-second tolerance should be rejected.");
        Expect(!MediaCompiler.IsDurationWithinTolerance(4.6, 4.0),
            "A long output outside the half-second tolerance should be rejected.");
        Expect(!MediaCompiler.IsDurationWithinTolerance(double.NaN, 4.0),
            "Non-finite measured durations should be rejected.");
    }

    private static async Task TestSingleInstancePipeAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var received = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeName = $"FortniteMatchCompiler.Tests-{Guid.NewGuid():N}";
        var listener = SingleInstanceCoordinator.ListenAsync(
            command => received.TrySetResult(command),
            cancellation.Token,
            pipeName);
        var command = $"synthetic-activate-{Guid.NewGuid():N}";

        try
        {
            await Task.Delay(75);
            Expect(SingleInstanceCoordinator.NotifyExisting(command, pipeName),
                "A second process should connect to the command pipe.");
            var delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Equal(command, delivered,
                "The single-instance command should arrive unchanged.");
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await listener.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task TestSingleInstanceShutdownWithoutContextPumpAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var pipeName = $"FortniteMatchCompiler.Tests-{Guid.NewGuid():N}";
        var nonPumpingContext = new NonPumpingSynchronizationContext();
        var callerContext = SynchronizationContext.Current;
        Task listener;

        try
        {
            SynchronizationContext.SetSynchronizationContext(nonPumpingContext);
            listener = SingleInstanceCoordinator.ListenAsync(
                _ => { },
                cancellation.Token,
                pipeName);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(callerContext);
        }

        cancellation.Cancel();
        var completed = await Task.WhenAny(
            listener,
            Task.Delay(TimeSpan.FromSeconds(3)));

        Expect(ReferenceEquals(completed, listener),
            "Cancelling the command listener must not depend on pumping the UI synchronization context.");
        try
        {
            await listener;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        Equal(0, nonPumpingContext.PostCount,
            "The command listener must not post its shutdown continuation to the caller's synchronization context.");
    }

    private static async Task TestFfmpegIntegrationAsync(SyntheticSandbox sandbox)
    {
        var tools = FfmpegLocator.Locate();
        var sourceFolder = sandbox.CreateDirectory("ffmpeg-source");
        var outputFolder = sandbox.CreateDirectory("ffmpeg-output");
        var start = new DateTime(2031, 8, 9, 18, 0, 0);
        var killPath = Path.Combine(
            sourceFolder,
            $"Fortnite {start:yyyy.MM.dd - HH.mm.ss}.01.Eliminierung.DVR.mp4");
        var resultTime = start.AddSeconds(3);
        var resultPath = Path.Combine(
            sourceFolder,
            $"Fortnite {resultTime:yyyy.MM.dd - HH.mm.ss}.02.Eliminiert.DVR.mp4");

        await CreateSyntheticClipAsync(tools.FfmpegPath, killPath, frequency: 440);
        await CreateSyntheticClipAsync(tools.FfmpegPath, resultPath, frequency: 660);
        File.SetLastWriteTimeUtc(killPath, DateTime.UtcNow - TimeSpan.FromSeconds(20));
        File.SetLastWriteTimeUtc(resultPath, DateTime.UtcNow - TimeSpan.FromSeconds(20));

        var match = new HighlightScanner().Scan(sourceFolder).LatestCompleted ??
                    throw new InvalidOperationException("Synthetic completed match was not detected.");
        Equal(2, match.Segments.Count,
            "The integration fixture should contain a kill and a result segment.");

        var result = await new MediaCompiler(tools).CompileAsync(
            match,
            new AppSettings
            {
                SourceFolder = sourceFolder,
                OutputFolder = outputFolder,
                TargetMegabytes = 2.0m,
                KillBeforeSeconds = 5.5m,
                KillAfterSeconds = 1.5m,
                ResultBeforeSeconds = 6.0m,
                ResultAfterSeconds = 2.0m
            },
            progress: null,
            CancellationToken.None);

        Expect(File.Exists(result.OutputPath),
            "The integration compilation should publish a final MP4.");
        Expect(result.SizeBytes < 2_000_000,
            "The integration compilation must stay strictly below two decimal megabytes.");
        Expect(result.DurationSeconds is > 3.8 and < 4.2,
            $"The two two-second fixtures should produce about four seconds, not {result.DurationSeconds:F3}.");
    }

    private static Task CreateSyntheticClipAsync(
        string ffmpegPath,
        string outputPath,
        int frequency)
    {
        return RunToolAsync(
            ffmpegPath,
            new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=2",
                "-f", "lavfi", "-i", $"sine=frequency={frequency}:sample_rate=48000:duration=2",
                "-map", "0:v:0", "-map", "1:a:0",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "96k", "-ar", "48000", "-ac", "2",
                "-movflags", "+faststart", "-shortest", outputPath
            });
    }

    private static async Task RunToolAsync(string executable, IReadOnlyList<string> arguments)
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

        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} exited with {process.ExitCode}.\n{error}\n{output}");
        }
    }

    private static HighlightItem Parse(string path)
    {
        Expect(HighlightScanner.TryParse(new FileInfo(path), out var item),
            $"Synthetic highlight filename did not parse: {Path.GetFileName(path)}");
        return item;
    }

    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine($"PASS: {name}");
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        await test();
        _passed++;
        Console.WriteLine($"PASS: {name}");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException(
                $"{message} Expected: {expected:F3}; actual: {actual:F3}.");
        }
    }

    private sealed class SyntheticSandbox : IDisposable
    {
        public SyntheticSandbox()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"FortniteMatchCompiler.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CreateDirectory(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateVideo(
            string folder,
            DateTime eventTime,
            int sequence,
            string label)
        {
            var fileName = string.Format(
                CultureInfo.InvariantCulture,
                "Fortnite {0:yyyy.MM.dd} - {0:HH.mm.ss}.{1:D2}.{2}.DVR.mp4",
                eventTime,
                sequence,
                label);
            return CreateFile(folder, fileName);
        }

        public string CreateScreenshot(
            string folder,
            DateTime eventTime,
            int sequence,
            string label)
        {
            var fileName = string.Format(
                CultureInfo.InvariantCulture,
                "Fortnite Screenshot {0:yyyy.MM.dd} - {0:HH.mm.ss}.{1:D2}.{2}.png",
                eventTime,
                sequence,
                label);
            return CreateFile(folder, fileName);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static string CreateFile(string folder, string fileName)
        {
            var path = Path.Combine(folder, fileName);
            File.WriteAllBytes(path, new byte[] { 0x46, 0x4D, 0x43 });
            return path;
        }
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _postCount);
        }
    }
}
