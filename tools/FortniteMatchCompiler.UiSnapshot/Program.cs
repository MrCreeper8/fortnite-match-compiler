using System.Globalization;
using System.Reflection;
using FortniteMatchCompiler;

namespace FortniteMatchCompiler.UiSnapshot;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1)
        {
            throw new ArgumentException("Usage: UiSnapshot <output-png>");
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");

        var settings = new AppSettings
        {
            SourceFolder = @"C:\Users\Player\AppData\Local\Temp\Highlights\Fortnite",
            OutputFolder = @"C:\Users\Player\Videos\Fortnite Compilations",
            TargetMegabytes = 10.0m,
            KillBeforeSeconds = 5.5m,
            KillAfterSeconds = 1.5m,
            ResultBeforeSeconds = 6.0m,
            ResultAfterSeconds = 2.0m
        };
        var latest = CreateDemoMatch();

        using var form = new MainForm(settings, autoCompile: false, scanOnShown: false);
        var matchField = typeof(MainForm).GetField(
            "_latestMatch",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingFieldException("MainForm._latestMatch");
        matchField.SetValue(form, latest);
        var updateMethod = typeof(MainForm).GetMethod(
            "UpdateMatchEstimate",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingMethodException("MainForm.UpdateMatchEstimate");
        updateMethod.Invoke(form, null);
        SetLabel(form, "_matchHeadline", latest.DisplaySummary);
        SetLabel(form, "_progressText", "Ready to compile the latest completed match.");
        var state = GetLabel(form, "_stateLabel");
        state.Text = "READY";
        state.ForeColor = Color.FromArgb(105, 224, 156);
        state.BackColor = Color.FromArgb(24, 56, 39);

        form.Show();
        var compileButton = (Button?)typeof(MainForm).GetField(
            "_compileButton",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) ??
            throw new MissingFieldException("MainForm._compileButton");
        compileButton.Select();
        var sourceFolder = (TextBox?)typeof(MainForm).GetField(
            "_sourceFolder",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) ??
            throw new MissingFieldException("MainForm._sourceFolder");
        sourceFolder.SelectionLength = 0;
        Application.DoEvents();
        form.PerformLayout();
        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        bitmap.Save(args[0], System.Drawing.Imaging.ImageFormat.Png);
        form.Hide();
        Console.WriteLine(Path.GetFullPath(args[0]));
    }

    private static void SetLabel(MainForm form, string fieldName, string text)
    {
        GetLabel(form, fieldName).Text = text;
    }

    private static Label GetLabel(MainForm form, string fieldName)
    {
        return (Label?)typeof(MainForm).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) ??
            throw new MissingFieldException($"MainForm.{fieldName}");
    }

    private static HighlightMatch CreateDemoMatch()
    {
        var start = new DateTime(2030, 1, 15, 19, 35, 0, DateTimeKind.Local);
        var items = Enumerable.Range(1, 10)
            .Select(index => CreateItem(
                start.AddSeconds(index * 70),
                index,
                "Elimination",
                HighlightKind.Kill,
                killCount: 1))
            .ToList();
        var victory = CreateItem(
            start.AddMinutes(13),
            11,
            "Victory",
            HighlightKind.Victory,
            killCount: 0);
        items.Add(victory);

        return new HighlightMatch
        {
            AllItems = items,
            Segments = items
                .Take(10)
                .Select(item => new CompilationSegment(item, SegmentRole.Kill))
                .Append(new CompilationSegment(victory, SegmentRole.Result))
                .ToArray(),
            Result = MatchResult.Victory,
            TerminalMarker = victory
        };
    }

    private static HighlightItem CreateItem(
        DateTime eventTime,
        int sequence,
        string label,
        HighlightKind kind,
        int killCount)
    {
        var fileName = $"Fortnite {eventTime:yyyy.MM.dd - HH.mm.ss}.{sequence:00}.{label}.DVR.mp4";
        return new HighlightItem(
            Path.Combine(@"C:\Demo\Highlights", fileName),
            fileName,
            eventTime,
            sequence,
            label,
            kind,
            killCount,
            IsScreenshot: false,
            Length: 30_000_000,
            LastWriteTimeUtc: DateTime.UtcNow.AddMinutes(-5));
    }
}
