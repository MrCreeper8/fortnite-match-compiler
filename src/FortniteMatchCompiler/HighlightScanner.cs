using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FortniteMatchCompiler;

public sealed class HighlightScanner
{
    private static readonly Regex VideoPattern = new(
        @"^Fortnite (?<date>\d{4}\.\d{2}\.\d{2}) - (?<time>\d{2}\.\d{2}\.\d{2})\.(?<sequence>\d+)\.(?<event>.+?)\.DVR\.mp4$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ScreenshotPattern = new(
        @"^Fortnite Screenshot (?<date>\d{4}\.\d{2}\.\d{2}) - (?<time>\d{2}\.\d{2}\.\d{2})\.(?<sequence>\d+)\.(?<event>.+?)\.png$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly TimeSpan ScreenshotGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DuplicateVictoryWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SessionBreak = TimeSpan.FromMinutes(90);

    public ScanResult Scan(string sourceFolder)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
        {
            throw new DirectoryNotFoundException(
                $"The NVIDIA Fortnite highlights folder was not found:\n{sourceFolder}");
        }

        var parsed = new List<HighlightItem>();
        var ignored = 0;

        foreach (var path in Directory.EnumerateFiles(sourceFolder))
        {
            var file = new FileInfo(path);
            if (TryParse(file, out var item))
            {
                parsed.Add(item);
            }
            else if (file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                     file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                ignored++;
            }
        }

        var victoryVideos = parsed
            .Where(item => !item.IsScreenshot && item.Kind == HighlightKind.Victory)
            .ToArray();

        var ordered = parsed
            .Where(item => !IsDuplicateVictoryScreenshot(item, victoryVideos))
            .OrderBy(item => item.EventTime)
            .ThenBy(item => item.IsScreenshot ? 0 : 1)
            .ThenBy(item => item.Sequence)
            .ToList();

        var completed = new List<HighlightMatch>();
        var unmatched = new List<HighlightItem>();
        var current = new List<HighlightItem>();
        HighlightItem? pendingVictoryScreenshot = null;

        foreach (var item in ordered)
        {
            if (pendingVictoryScreenshot is not null &&
                !item.IsScreenshot &&
                item.Kind == HighlightKind.Eliminated &&
                item.EventTime <= pendingVictoryScreenshot.EventTime + ScreenshotGrace)
            {
                // Contradictory terminal markers within a few seconds are delayed media from
                // one round, not evidence that a whole new match finished. Keep the clear win
                // boundary and quarantine the conflicting loss instead of creating a fake match.
                completed.Add(BuildMatch(current, MatchResult.Victory, pendingVictoryScreenshot));
                current = new List<HighlightItem>();
                pendingVictoryScreenshot = null;
                unmatched.Add(item);
                continue;
            }

            if (pendingVictoryScreenshot is not null &&
                item.EventTime > pendingVictoryScreenshot.EventTime + ScreenshotGrace)
            {
                completed.Add(BuildMatch(current, MatchResult.Victory, pendingVictoryScreenshot));
                current = new List<HighlightItem>();
                pendingVictoryScreenshot = null;
            }

            if (current.Count > 0 && IsSessionBreak(current[^1], item))
            {
                // A missing terminal marker cannot be reconstructed reliably. Quarantine the
                // earlier highlights instead of silently merging matches across days/sessions.
                unmatched.AddRange(current);
                current = new List<HighlightItem>();
                pendingVictoryScreenshot = null;
            }

            current.Add(item);

            if (!item.IsScreenshot && item.Kind == HighlightKind.Eliminated)
            {
                completed.Add(BuildMatch(current, MatchResult.Eliminated, item));
                current = new List<HighlightItem>();
                pendingVictoryScreenshot = null;
            }
            else if (!item.IsScreenshot && item.Kind == HighlightKind.Victory)
            {
                completed.Add(BuildMatch(current, MatchResult.Victory, item));
                current = new List<HighlightItem>();
                pendingVictoryScreenshot = null;
            }
            else if (item.IsScreenshot && item.Kind == HighlightKind.Victory)
            {
                pendingVictoryScreenshot = item;
            }
        }

        if (pendingVictoryScreenshot is not null &&
            DateTime.UtcNow - pendingVictoryScreenshot.LastWriteTimeUtc >= ScreenshotGrace)
        {
            completed.Add(BuildMatch(current, MatchResult.Victory, pendingVictoryScreenshot));
            current = new List<HighlightItem>();
        }

        return new ScanResult
        {
            CompletedMatches = completed,
            UnfinishedItems = unmatched.Concat(current).ToArray(),
            IgnoredFileCount = ignored
        };
    }

    public static bool TryParse(FileInfo file, out HighlightItem item)
    {
        item = null!;
        var isScreenshot = false;
        var match = VideoPattern.Match(file.Name);
        if (!match.Success)
        {
            match = ScreenshotPattern.Match(file.Name);
            isScreenshot = match.Success;
        }

        if (!match.Success)
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                $"{match.Groups["date"].Value} {match.Groups["time"].Value}",
                "yyyy.MM.dd HH.mm.ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var eventTime) ||
            !int.TryParse(match.Groups["sequence"].Value, out var sequence))
        {
            return false;
        }

        var label = match.Groups["event"].Value;
        var kind = Classify(label);
        item = new HighlightItem(
            file.FullName,
            file.Name,
            eventTime,
            sequence,
            label,
            kind,
            CountKills(label, kind),
            isScreenshot,
            file.Length,
            file.LastWriteTimeUtc);
        return true;
    }

    public static HighlightKind Classify(string label)
    {
        var normalized = Normalize(label);

        // Exact result labels must be checked before elimination substrings.
        if (normalized is "eliminiert" or "eliminated" or "player eliminated")
        {
            return HighlightKind.Eliminated;
        }

        if (normalized is "sieg" or "victory" or "victory royale")
        {
            return HighlightKind.Victory;
        }

        if (normalized.Contains("eliminierung", StringComparison.Ordinal) ||
            normalized.Contains("eliminerung", StringComparison.Ordinal) ||
            normalized.Contains("elimination", StringComparison.Ordinal))
        {
            return HighlightKind.Kill;
        }

        if (normalized.Contains("am boden", StringComparison.Ordinal) ||
            normalized.Contains("knock", StringComparison.Ordinal) ||
            normalized.Contains("downed", StringComparison.Ordinal))
        {
            return HighlightKind.Knock;
        }

        return HighlightKind.Other;
    }

    private static int CountKills(string label, HighlightKind kind)
    {
        if (kind != HighlightKind.Kill)
        {
            return 0;
        }

        var normalized = Normalize(label);
        if (normalized.Contains("dreifach", StringComparison.Ordinal) ||
            normalized.Contains("triple", StringComparison.Ordinal))
        {
            return 3;
        }

        if (normalized.Contains("doppel", StringComparison.Ordinal) ||
            normalized.Contains("double", StringComparison.Ordinal))
        {
            return 2;
        }

        if (normalized.Contains("vierfach", StringComparison.Ordinal) ||
            normalized.Contains("quad", StringComparison.Ordinal))
        {
            return 4;
        }

        return 1;
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsDuplicateVictoryScreenshot(
        HighlightItem item,
        IReadOnlyCollection<HighlightItem> victoryVideos)
    {
        return item.IsScreenshot &&
               item.Kind == HighlightKind.Victory &&
               victoryVideos.Any(video =>
                   Math.Abs((video.EventTime - item.EventTime).TotalSeconds) <=
                   DuplicateVictoryWindow.TotalSeconds);
    }

    private static bool IsSessionBreak(HighlightItem previous, HighlightItem next)
    {
        var gap = next.EventTime - previous.EventTime;
        return gap > SessionBreak;
    }

    private static HighlightMatch BuildMatch(
        IReadOnlyList<HighlightItem> items,
        MatchResult result,
        HighlightItem terminalMarker)
    {
        if (items.Count == 0)
        {
            throw new InvalidOperationException("Cannot close an empty match.");
        }

        var kills = items
            .Where(item => !item.IsScreenshot && item.Kind == HighlightKind.Kill)
            .OrderBy(item => item.EventTime)
            .ThenBy(item => item.Sequence)
            .ToList();

        HighlightItem terminalMedia = terminalMarker;
        if (terminalMarker.IsScreenshot)
        {
            terminalMedia = items
                .Where(item => !item.IsScreenshot &&
                               item.Kind is HighlightKind.Kill or HighlightKind.Knock or HighlightKind.Victory &&
                               Math.Abs((item.EventTime - terminalMarker.EventTime).TotalSeconds) <=
                               ScreenshotGrace.TotalSeconds)
                .OrderBy(item => Math.Abs((item.EventTime - terminalMarker.EventTime).TotalSeconds))
                .ThenBy(item => item.EventTime < terminalMarker.EventTime ? 1 : 0)
                .ThenByDescending(item => item.EventTime)
                .FirstOrDefault() ?? terminalMarker;
        }

        // A screenshot-only win can be followed by a clip containing the victory moment.
        // Keep that clip exactly once and force it to the compilation's end.
        kills.RemoveAll(item =>
            string.Equals(item.FullPath, terminalMedia.FullPath, StringComparison.OrdinalIgnoreCase));

        var segments = kills
            .Select(item => new CompilationSegment(item, SegmentRole.Kill))
            .Append(new CompilationSegment(terminalMedia, SegmentRole.Result))
            .ToArray();

        return new HighlightMatch
        {
            AllItems = items.ToArray(),
            Segments = segments,
            Result = result,
            TerminalMarker = terminalMarker
        };
    }
}
