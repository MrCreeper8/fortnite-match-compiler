using System.Globalization;

namespace FortniteMatchCompiler;

public enum HighlightKind
{
    Kill,
    Knock,
    Victory,
    Eliminated,
    Other
}

public enum MatchResult
{
    Victory,
    Eliminated
}

public enum SegmentRole
{
    Kill,
    Result
}

public sealed record HighlightItem(
    string FullPath,
    string FileName,
    DateTime EventTime,
    int Sequence,
    string EventLabel,
    HighlightKind Kind,
    int KillCount,
    bool IsScreenshot,
    long Length,
    DateTime LastWriteTimeUtc);

public sealed record CompilationSegment(HighlightItem Item, SegmentRole Role);

public sealed class HighlightMatch
{
    public required IReadOnlyList<HighlightItem> AllItems { get; init; }
    public required IReadOnlyList<CompilationSegment> Segments { get; init; }
    public required MatchResult Result { get; init; }
    public required HighlightItem TerminalMarker { get; init; }

    public DateTime StartTime => AllItems.Min(item => item.EventTime);
    public DateTime EndTime => AllItems.Max(item => item.EventTime);
    public int KillCount => AllItems
        .Where(item => !item.IsScreenshot && item.Kind == HighlightKind.Kill)
        .Sum(item => item.KillCount);
    public int KillClipCount => AllItems
        .Count(item => !item.IsScreenshot && item.Kind == HighlightKind.Kill);

    public string ResultDisplay => Result == MatchResult.Victory ? "Victory" : "Eliminated";

    public string DisplaySummary => string.Format(
        CultureInfo.CurrentCulture,
        "{0} kill{1} in {2} clip{3}  •  {4}  •  {5:HH:mm}–{6:HH:mm}",
        KillCount,
        KillCount == 1 ? string.Empty : "s",
        KillClipCount,
        KillClipCount == 1 ? string.Empty : "s",
        ResultDisplay,
        StartTime,
        EndTime);
}

public sealed class ScanResult
{
    public required IReadOnlyList<HighlightMatch> CompletedMatches { get; init; }
    public required IReadOnlyList<HighlightItem> UnfinishedItems { get; init; }
    public required int IgnoredFileCount { get; init; }

    public HighlightMatch? LatestCompleted => CompletedMatches.LastOrDefault();
    public bool HasNewerUnfinishedMatch => LatestCompleted is not null &&
        UnfinishedItems.Any(item =>
            item.EventTime > LatestCompleted.EndTime &&
            (!item.IsScreenshot || item.Kind == HighlightKind.Victory));
}

public sealed record MediaProbe(double DurationSeconds, bool HasVideo, bool HasAudio);

public sealed record ClipWindow(
    double EventSeconds,
    double StartSeconds,
    double DurationSeconds);

public sealed record PreparedSegment(
    CompilationSegment Segment,
    MediaProbe Probe,
    double StartSeconds,
    double DurationSeconds);

public sealed record CompileProgress(int Percent, string Message);

public sealed class CompileResult
{
    public required string OutputPath { get; init; }
    public required long SizeBytes { get; init; }
    public required double DurationSeconds { get; init; }
    public required bool UsedExistingFile { get; init; }
}
