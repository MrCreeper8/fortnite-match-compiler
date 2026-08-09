namespace FortniteMatchCompiler;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        NativeProcessErrorMode.SuppressChildProcessCrashDialogs();

        var autoCompile = args.Any(argument =>
            string.Equals(argument, "--compile-latest", StringComparison.OrdinalIgnoreCase));
        if (args.Any(argument =>
                string.Equals(argument, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = RunSmokeTest();
            return;
        }

        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: @"Local\FortniteMatchCompiler-SingleInstance",
            createdNew: out var createdNew);

        if (!createdNew)
        {
            var command = autoCompile ? "compile-latest" : "activate";
            if (!SingleInstanceCoordinator.NotifyExisting(command))
            {
                MessageBox.Show(
                    "Fortnite Match Compiler is already open.",
                    "Fortnite Match Compiler",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            return;
        }

        ApplicationConfiguration.Initialize();
        using var listenerCancellation = new CancellationTokenSource();
        var form = new MainForm(AppSettings.Load(), autoCompile);
        _ = form.Handle;
        var listener = SingleInstanceCoordinator.ListenAsync(command =>
        {
            if (!form.IsDisposed && form.IsHandleCreated)
            {
                form.BeginInvoke(new Action(() => form.HandleExternalCommand(command)));
            }
        }, listenerCancellation.Token);
        Application.Run(form);
        listenerCancellation.Cancel();
        try
        {
            if (listener.Wait(TimeSpan.FromSeconds(1)))
            {
                listener.GetAwaiter().GetResult();
            }
            else
            {
                AppLogger.Write("Single-instance command listener did not stop before shutdown; exiting anyway.");
            }
        }
        catch (OperationCanceledException)
        {
        }

        GC.KeepAlive(singleInstance);
    }

    private static int RunSmokeTest()
    {
        try
        {
            ApplicationConfiguration.Initialize();
            var settings = AppSettings.Load();
            _ = FfmpegLocator.Locate();
            var latest = new HighlightScanner().Scan(settings.SourceFolder).LatestCompleted;
            if (latest is null)
            {
                throw new InvalidOperationException("No completed match was detected.");
            }

            using var form = new MainForm(settings, autoCompile: false);
            form.CreateControl();
            AppLogger.Write($"Smoke test passed: {latest.DisplaySummary}");
            return 0;
        }
        catch (Exception exception)
        {
            AppLogger.Write($"Smoke test failed: {exception}");
            return 2;
        }
    }
}
