using System.Diagnostics;

namespace FortniteMatchCompiler;

public sealed class MainForm : Form
{
    private static readonly Color WindowColor = Color.FromArgb(18, 20, 26);
    private static readonly Color CardColor = Color.FromArgb(29, 32, 41);
    private static readonly Color FieldColor = Color.FromArgb(39, 43, 54);
    private static readonly Color TextColor = Color.FromArgb(241, 244, 248);
    private static readonly Color MutedColor = Color.FromArgb(164, 172, 187);
    private static readonly Color AccentColor = Color.FromArgb(105, 224, 156);
    private static readonly Color PurpleColor = Color.FromArgb(151, 117, 250);
    private static readonly Color WarningColor = Color.FromArgb(255, 194, 92);

    private readonly AppSettings _settings;
    private readonly bool _autoCompile;
    private readonly HighlightScanner _scanner = new();
    private readonly Label _stateLabel = new();
    private readonly Label _matchHeadline = new();
    private readonly Label _matchDetails = new();
    private readonly Label _matchFootnote = new();
    private readonly TextBox _sourceFolder = new();
    private readonly TextBox _outputFolder = new();
    private readonly NumericUpDown _maximumSize = new();
    private readonly NumericUpDown _killBeforeSeconds = new();
    private readonly NumericUpDown _killAfterSeconds = new();
    private readonly NumericUpDown _resultBeforeSeconds = new();
    private readonly NumericUpDown _resultAfterSeconds = new();
    private readonly Button _refreshButton = new();
    private readonly Button _compileButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _openFolderButton = new();
    private readonly Button _playButton = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Label _progressText = new();
    private readonly Label _outputText = new();

    private HighlightMatch? _latestMatch;
    private CancellationTokenSource? _compilationCancellation;
    private string? _lastOutputPath;
    private bool _isBusy;
    private bool _autoCompileStarted;
    private bool _closeWhenIdle;
    private bool _allowClose;
    private bool _compileWhenIdle;

    public MainForm(AppSettings settings, bool autoCompile, bool scanOnShown = true)
    {
        _settings = settings;
        _autoCompile = autoCompile;
        InitializeWindow();
        BuildInterface();
        LoadSettingsIntoControls();
        if (scanOnShown)
        {
            Shown += async (_, _) => await RefreshMatchesAsync();
        }
        FormClosing += OnFormClosing;
    }

    public void HandleExternalCommand(string command)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Show();
        Activate();
        BringToFront();

        if (string.Equals(command, "compile-latest", StringComparison.OrdinalIgnoreCase))
        {
            _autoCompileStarted = true;
            if (_isBusy)
            {
                _compileWhenIdle = true;
                _progressText.Text = "Compile latest is queued and will start when the current operation finishes.";
            }
            else
            {
                _ = RefreshAndCompileAsync();
            }
        }
    }

    private void InitializeWindow()
    {
        Text = "Fortnite Match Compiler";
        BackColor = WindowColor;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 650);
        MinimumSize = new Size(760, 630);
        AutoScaleMode = AutoScaleMode.Dpi;

        try
        {
            var executableIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (executableIcon is not null)
            {
                Icon = executableIcon;
            }
        }
        catch (Exception exception)
        {
            // A missing shell icon should never stop the compiler from opening.
            AppLogger.Write($"Could not load the application icon: {exception.Message}");
        }
    }

    private void BuildInterface()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 18, 24, 16),
            ColumnCount = 1,
            RowCount = 6,
            BackColor = WindowColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 135));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 176));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildMatchCard(), 0, 1);
        root.Controls.Add(BuildSettingsCard(), 0, 2);
        root.Controls.Add(BuildActionRow(), 0, 3);
        root.Controls.Add(BuildProgressPanel(), 0, 4);
        root.Controls.Add(BuildOutputPanel(), 0, 5);

    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = WindowColor };
        var title = new Label
        {
            AutoSize = true,
            Location = new Point(0, 2),
            Text = "Fortnite Match Compiler",
            ForeColor = TextColor,
            Font = new Font("Segoe UI Semibold", 22f, FontStyle.Bold, GraphicsUnit.Point)
        };
        var subtitle = new Label
        {
            AutoSize = true,
            Location = new Point(3, 43),
            Text = "Turn one completed match into one small, watchable video. Original clips stay untouched.",
            ForeColor = MutedColor,
            Font = new Font("Segoe UI", 10f)
        };
        _stateLabel.AutoSize = false;
        _stateLabel.Size = new Size(112, 30);
        _stateLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _stateLabel.Location = new Point(636, 12);
        _stateLabel.TextAlign = ContentAlignment.MiddleCenter;
        _stateLabel.BackColor = FieldColor;
        _stateLabel.ForeColor = MutedColor;
        _stateLabel.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
        _stateLabel.Text = "SCANNING";
        panel.Resize += (_, _) => _stateLabel.Left = Math.Max(0, panel.ClientSize.Width - _stateLabel.Width);
        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        panel.Controls.Add(_stateLabel);
        return panel;
    }

    private Control BuildMatchCard()
    {
        var card = CreateCard(new Padding(18, 14, 18, 12));
        var label = new Label
        {
            AutoSize = true,
            Location = new Point(18, 13),
            Text = "LATEST COMPLETED MATCH",
            ForeColor = PurpleColor,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold)
        };
        _matchHeadline.AutoSize = true;
        _matchHeadline.Location = new Point(18, 39);
        _matchHeadline.Text = "Scanning NVIDIA highlights…";
        _matchHeadline.ForeColor = TextColor;
        _matchHeadline.Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold);
        _matchDetails.AutoSize = true;
        _matchDetails.Location = new Point(19, 73);
        _matchDetails.Text = "";
        _matchDetails.ForeColor = MutedColor;
        _matchDetails.Font = new Font("Segoe UI", 10f);
        _matchFootnote.AutoSize = true;
        _matchFootnote.Location = new Point(19, 99);
        _matchFootnote.Text = "";
        _matchFootnote.ForeColor = MutedColor;
        _matchFootnote.Font = new Font("Segoe UI", 9f);
        card.Controls.Add(label);
        card.Controls.Add(_matchHeadline);
        card.Controls.Add(_matchDetails);
        card.Controls.Add(_matchFootnote);
        return card;
    }

    private Control BuildSettingsCard()
    {
        var card = CreateCard(new Padding(16, 13, 16, 12));
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = CardColor,
            ColumnCount = 3,
            RowCount = 4,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(layout);

        ConfigurePathTextBox(_sourceFolder);
        ConfigurePathTextBox(_outputFolder);
        _sourceFolder.TextChanged += (_, _) => InvalidateDetectedMatch();
        layout.Controls.Add(CreateFieldLabel("Source folder"), 0, 0);
        layout.Controls.Add(_sourceFolder, 1, 0);
        layout.Controls.Add(CreateSmallButton("Browse…", (_, _) => BrowseForFolder(_sourceFolder)), 2, 0);
        layout.Controls.Add(CreateFieldLabel("Save videos to"), 0, 1);
        layout.Controls.Add(_outputFolder, 1, 1);
        layout.Controls.Add(CreateSmallButton("Browse…", (_, _) => BrowseForFolder(_outputFolder)), 2, 1);

        ConfigureNumber(_maximumSize, 1, 500, 0.5m, 1);
        ConfigureNumber(_killBeforeSeconds, 0.5m, 30, 0.5m, 1);
        ConfigureNumber(_killAfterSeconds, 0.5m, 30, 0.5m, 1);
        ConfigureNumber(_resultBeforeSeconds, 0.5m, 30, 0.5m, 1);
        ConfigureNumber(_resultAfterSeconds, 0.5m, 30, 0.5m, 1);
        _maximumSize.Width = 68;
        _killBeforeSeconds.Width = 52;
        _killAfterSeconds.Width = 52;
        _resultBeforeSeconds.Width = 52;
        _resultAfterSeconds.Width = 52;

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = CardColor,
            Padding = new Padding(0, 4, 0, 0)
        };
        options.Controls.Add(CreateInlineLabel("Max"));
        options.Controls.Add(_maximumSize);
        options.Controls.Add(CreateInlineLabel("MB   Kill"));
        options.Controls.Add(_killBeforeSeconds);
        options.Controls.Add(CreateInlineLabel("s before"));
        options.Controls.Add(_killAfterSeconds);
        options.Controls.Add(CreateInlineLabel("s after   Result"));
        options.Controls.Add(_resultBeforeSeconds);
        options.Controls.Add(CreateInlineLabel("s before"));
        options.Controls.Add(_resultAfterSeconds);
        options.Controls.Add(CreateInlineLabel("s after"));
        layout.Controls.Add(options, 0, 2);
        layout.SetColumnSpan(options, 3);

        var explanation = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Timing is relative to the event; multi-kill clips automatically include more action beforehand.",
            ForeColor = MutedColor,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 8.7f)
        };
        layout.Controls.Add(explanation, 0, 3);
        layout.SetColumnSpan(explanation, 3);

        _maximumSize.ValueChanged += (_, _) => UpdateMatchEstimate();
        _killBeforeSeconds.ValueChanged += (_, _) => UpdateMatchEstimate();
        _killAfterSeconds.ValueChanged += (_, _) => UpdateMatchEstimate();
        _resultBeforeSeconds.ValueChanged += (_, _) => UpdateMatchEstimate();
        _resultAfterSeconds.ValueChanged += (_, _) => UpdateMatchEstimate();
        return card;
    }

    private Control BuildActionRow()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 12, 0, 6),
            BackColor = WindowColor
        };

        ConfigureButton(_compileButton, "Compile latest match", AccentColor, Color.FromArgb(12, 38, 25), 196);
        _compileButton.Click += (_, _) => BeginCompilation();
        ConfigureButton(_refreshButton, "Refresh", FieldColor, TextColor, 94);
        _refreshButton.Click += async (_, _) => await RefreshMatchesAsync();
        ConfigureButton(_cancelButton, "Cancel", FieldColor, WarningColor, 94);
        _cancelButton.Enabled = false;
        _cancelButton.Click += (_, _) => _compilationCancellation?.Cancel();
        ConfigureButton(_playButton, "Play video", FieldColor, TextColor, 102);
        _playButton.Enabled = false;
        _playButton.Click += (_, _) => OpenLastVideo();
        ConfigureButton(_openFolderButton, "Open folder", FieldColor, TextColor, 112);
        _openFolderButton.Click += (_, _) => OpenOutputFolder();

        panel.Controls.Add(_compileButton);
        panel.Controls.Add(_refreshButton);
        panel.Controls.Add(_cancelButton);
        panel.Controls.Add(_playButton);
        panel.Controls.Add(_openFolderButton);
        return panel;
    }

    private Control BuildProgressPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = WindowColor };
        _progressBar.Dock = DockStyle.Bottom;
        _progressBar.Height = 12;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressText.Dock = DockStyle.Top;
        _progressText.Height = 28;
        _progressText.Text = "Ready.";
        _progressText.ForeColor = MutedColor;
        _progressText.TextAlign = ContentAlignment.MiddleLeft;
        panel.Controls.Add(_progressText);
        panel.Controls.Add(_progressBar);
        return panel;
    }

    private Control BuildOutputPanel()
    {
        _outputText.Dock = DockStyle.Fill;
        _outputText.ForeColor = MutedColor;
        _outputText.TextAlign = ContentAlignment.TopLeft;
        _outputText.Padding = new Padding(2, 8, 2, 0);
        _outputText.AutoEllipsis = true;
        _outputText.Text = "Finished compilations will appear here.";
        return _outputText;
    }

    private void LoadSettingsIntoControls()
    {
        _sourceFolder.Text = _settings.SourceFolder;
        _outputFolder.Text = _settings.OutputFolder;
        SetNumericValue(_maximumSize, _settings.TargetMegabytes);
        SetNumericValue(_killBeforeSeconds, _settings.KillBeforeSeconds);
        SetNumericValue(_killAfterSeconds, _settings.KillAfterSeconds);
        SetNumericValue(_resultBeforeSeconds, _settings.ResultBeforeSeconds);
        SetNumericValue(_resultAfterSeconds, _settings.ResultAfterSeconds);
    }

    private async Task RefreshMatchesAsync()
    {
        if (_isBusy)
        {
            return;
        }

        SetBusy(true, canCancel: false);
        SetState("SCANNING", MutedColor, FieldColor);
        _progressText.Text = "Scanning NVIDIA highlights…";
        _progressBar.Value = 0;
        _matchHeadline.Text = "Scanning NVIDIA highlights…";
        _matchDetails.Text = string.Empty;
        _matchFootnote.Text = string.Empty;

        try
        {
            var source = _sourceFolder.Text.Trim();
            var result = await Task.Run(() => _scanner.Scan(source));
            _latestMatch = result.LatestCompleted;

            if (_latestMatch is null)
            {
                SetState("NO MATCH", WarningColor, FieldColor);
                _matchHeadline.Text = "No completed match found";
                _matchDetails.Text = "A match is complete after NVIDIA saves a Sieg/Victory or Eliminiert/Eliminated event.";
                _progressText.Text = "Nothing to compile yet.";
            }
            else if (result.HasNewerUnfinishedMatch)
            {
                SetState("IN PROGRESS", WarningColor, FieldColor);
                _matchHeadline.Text = "A newer match appears to still be in progress";
                _matchDetails.Text = "Wait for the Victory or Eliminated highlight, then press Refresh.";
                _matchFootnote.Text = $"Last completed: {_latestMatch.DisplaySummary}";
                _latestMatch = null;
                _progressText.Text = "Waiting for the current match to finish.";
            }
            else
            {
                SetState("READY", AccentColor, Color.FromArgb(24, 56, 39));
                _matchHeadline.Text = _latestMatch.DisplaySummary;
                UpdateMatchEstimate();
                _progressText.Text = "Ready to compile the latest completed match.";
            }

            SaveControlsToSettings();
            TrySaveSettings();
        }
        catch (Exception exception)
        {
            _latestMatch = null;
            SetState("ERROR", Color.FromArgb(255, 130, 130), FieldColor);
            _matchHeadline.Text = "Could not scan the highlights folder";
            _matchDetails.Text = exception.Message.Replace(Environment.NewLine, "  ");
            _progressText.Text = "Choose the correct NVIDIA highlights folder and press Refresh.";
            AppLogger.Write($"Scan failed: {exception}");
        }
        finally
        {
            SetBusy(false, canCancel: false);
        }

        if (_closeWhenIdle)
        {
            CloseWhenSafe();
            return;
        }

        if (TryRunQueuedCompilation())
        {
            return;
        }

        if (_autoCompile && !_autoCompileStarted && _latestMatch is not null)
        {
            _autoCompileStarted = true;
            await CompileLatestAsync();
        }
    }

    private async Task RefreshAndCompileAsync()
    {
        await RefreshMatchesAsync();
        if (!_isBusy && _latestMatch is not null &&
            !_closeWhenIdle && !_allowClose && !IsDisposed)
        {
            BeginCompilation();
        }
    }

    private void UpdateMatchEstimate()
    {
        if (_latestMatch is null)
        {
            return;
        }

        var seconds = _latestMatch.Segments.Sum(segment =>
            segment.Role == SegmentRole.Result
                ? (double)(_resultBeforeSeconds.Value + _resultAfterSeconds.Value)
                : (double)(_killBeforeSeconds.Value + _killAfterSeconds.Value) +
                  Math.Max(0, segment.Item.KillCount - 1) * 4.0);
        _matchDetails.Text =
            $"{_latestMatch.Segments.Count} selected clips  •  about {FormatDuration(seconds)}  •  under {_maximumSize.Value:0.0} MB";
        _matchFootnote.Text = _latestMatch.Result == MatchResult.Victory
            ? "Kills and multi-kills, followed by the win. Knock clips are skipped unless they contain a screenshot-only victory."
            : "Kills and multi-kills, followed by your final elimination. Knock clips are skipped.";
    }

    private async Task CompileLatestAsync()
    {
        if (_latestMatch is null || _isBusy)
        {
            return;
        }

        _compilationCancellation = new CancellationTokenSource();
        SetBusy(true, canCancel: true);
        SetState("ENCODING", PurpleColor, Color.FromArgb(49, 38, 82));
        _progressBar.Value = 0;
        _outputText.Text = "Working… the two encoding passes usually take less than a minute.";

        var progress = new Progress<CompileProgress>(update =>
        {
            _progressBar.Value = Math.Clamp(update.Percent, 0, 100);
            _progressText.Text = update.Message;
        });

        try
        {
            SaveControlsToSettings();
            _settings.Save();

            // Always resolve "latest" again at click time. The app may have been left open
            // while another match finished, or the source folder may have changed.
            _progressText.Text = "Confirming the latest completed match…";
            var scan = await Task.Run(() => _scanner.Scan(_settings.SourceFolder));
            if (scan.LatestCompleted is null)
            {
                _latestMatch = null;
                throw new InvalidOperationException("No completed match was found in the selected highlights folder.");
            }

            if (scan.HasNewerUnfinishedMatch)
            {
                _latestMatch = null;
                throw new InvalidOperationException(
                    "A newer match appears to still be in progress. Wait for NVIDIA to save Victory or Eliminated, then try again.");
            }

            var matchToCompile = scan.LatestCompleted;
            _latestMatch = matchToCompile;
            _matchHeadline.Text = matchToCompile.DisplaySummary;
            UpdateMatchEstimate();

            var tools = FfmpegLocator.Locate();
            var compiler = new MediaCompiler(tools);
            var result = await compiler.CompileAsync(
                matchToCompile,
                _settings,
                progress,
                _compilationCancellation.Token);

            _lastOutputPath = result.OutputPath;
            _playButton.Enabled = true;
            _progressBar.Value = 100;
            SetState("DONE", AccentColor, Color.FromArgb(24, 56, 39));
            var size = result.SizeBytes / 1_000_000.0;
            _progressText.Text = result.UsedExistingFile
                ? "This match was already compiled."
                : "Compilation complete.";
            _outputText.Text =
                $"{(result.UsedExistingFile ? "Existing video" : "Saved")}  •  {size:0.00} MB  •  " +
                $"{FormatDuration(result.DurationSeconds)}{Environment.NewLine}{result.OutputPath}";
        }
        catch (OperationCanceledException)
        {
            SetState("CANCELLED", WarningColor, FieldColor);
            _progressBar.Value = 0;
            _progressText.Text = "Compilation cancelled. You can safely try again.";
            _outputText.Text = "No finished video was published.";
        }
        catch (Exception exception)
        {
            SetState("ERROR", Color.FromArgb(255, 130, 130), FieldColor);
            _progressBar.Value = 0;
            _progressText.Text = "Compilation failed.";
            _outputText.Text = exception.Message;
            MessageBox.Show(
                this,
                exception.Message,
                "Could not compile the match",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _compilationCancellation?.Dispose();
            _compilationCancellation = null;
            SetBusy(false, canCancel: false);
            if (_closeWhenIdle)
            {
                CloseWhenSafe();
            }
            else
            {
                _ = TryRunQueuedCompilation();
            }
        }
    }

    private void BeginCompilation()
    {
        if (!_isBusy && _latestMatch is not null)
        {
            _ = CompileLatestAsync();
        }
    }

    private bool TryRunQueuedCompilation()
    {
        if (!_compileWhenIdle || _isBusy || _closeWhenIdle || _allowClose || IsDisposed)
        {
            return false;
        }

        _compileWhenIdle = false;
        if (_latestMatch is null)
        {
            return false;
        }

        BeginCompilation();
        return true;
    }

    private void SaveControlsToSettings()
    {
        _settings.SourceFolder = _sourceFolder.Text.Trim();
        _settings.OutputFolder = _outputFolder.Text.Trim();
        _settings.TargetMegabytes = _maximumSize.Value;
        _settings.KillBeforeSeconds = _killBeforeSeconds.Value;
        _settings.KillAfterSeconds = _killAfterSeconds.Value;
        _settings.ResultBeforeSeconds = _resultBeforeSeconds.Value;
        _settings.ResultAfterSeconds = _resultAfterSeconds.Value;
    }

    private void SetBusy(bool busy, bool canCancel)
    {
        _isBusy = busy;
        _compileButton.Enabled = !busy && _latestMatch is not null;
        _refreshButton.Enabled = !busy;
        _sourceFolder.Enabled = !busy;
        _outputFolder.Enabled = !busy;
        _maximumSize.Enabled = !busy;
        _killBeforeSeconds.Enabled = !busy;
        _killAfterSeconds.Enabled = !busy;
        _resultBeforeSeconds.Enabled = !busy;
        _resultAfterSeconds.Enabled = !busy;
        _cancelButton.Enabled = busy && canCancel;
    }

    private void SetState(string text, Color foreground, Color background)
    {
        _stateLabel.Text = text;
        _stateLabel.ForeColor = foreground;
        _stateLabel.BackColor = background;
    }

    private void BrowseForFolder(TextBox target)
    {
        using var dialog = new FolderBrowserDialog
        {
            InitialDirectory = Directory.Exists(target.Text) ? target.Text : string.Empty,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = dialog.SelectedPath;
        }
    }

    private void OpenLastVideo()
    {
        try
        {
            if (_lastOutputPath is null || !File.Exists(_lastOutputPath))
            {
                return;
            }

            Process.Start(new ProcessStartInfo(_lastOutputPath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowOperationError("Could not open the video", exception);
        }
    }

    private void OpenOutputFolder()
    {
        try
        {
            var folder = _outputFolder.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            Directory.CreateDirectory(folder);
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            if (_lastOutputPath is not null && File.Exists(_lastOutputPath))
            {
                startInfo.ArgumentList.Add($"/select,{_lastOutputPath}");
            }
            else
            {
                startInfo.ArgumentList.Add(folder);
            }

            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            ShowOperationError("Could not open the output folder", exception);
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_allowClose)
        {
            return;
        }

        if (!_isBusy)
        {
            SaveControlsToSettings();
            TrySaveSettings();
            return;
        }

        eventArgs.Cancel = true;

        if (_compilationCancellation is null)
        {
            _closeWhenIdle = true;
            _progressText.Text = "Closing after the scan finishes…";
            return;
        }

        var choice = MessageBox.Show(
            this,
            "A compilation is still running. Cancel it and close?",
            "Fortnite Match Compiler",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (choice == DialogResult.Yes)
        {
            _closeWhenIdle = true;
            _progressText.Text = "Cancelling safely…";
            _cancelButton.Enabled = false;
            _compilationCancellation.Cancel();
        }
    }

    private void InvalidateDetectedMatch()
    {
        if (_isBusy || !IsHandleCreated)
        {
            return;
        }

        _latestMatch = null;
        _compileButton.Enabled = false;
        SetState("REFRESH", WarningColor, FieldColor);
        _matchHeadline.Text = "Source folder changed";
        _matchDetails.Text = "Press Refresh to detect the latest completed match in this folder.";
        _matchFootnote.Text = string.Empty;
    }

    private void TrySaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception exception)
        {
            AppLogger.Write($"Could not save settings: {exception}");
        }
    }

    private void CloseWhenSafe()
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        _allowClose = true;
        BeginInvoke(new Action(Close));
    }

    private void ShowOperationError(string title, Exception exception)
    {
        AppLogger.Write($"{title}: {exception}");
        MessageBox.Show(
            this,
            exception.Message,
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static Panel CreateCard(Padding padding)
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 8),
            Padding = padding,
            BackColor = CardColor
        };
    }

    private static Label CreateFieldLabel(string text)
    {
        return new Label
        {
            Dock = DockStyle.Fill,
            Text = text,
            ForeColor = MutedColor,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private static Label CreateInlineLabel(string text)
    {
        return new Label
        {
            AutoSize = true,
            Text = text,
            ForeColor = MutedColor,
            Margin = new Padding(0, 6, 5, 0)
        };
    }

    private static void ConfigurePathTextBox(TextBox textBox)
    {
        textBox.Dock = DockStyle.Fill;
        textBox.Margin = new Padding(0, 4, 8, 4);
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.BackColor = FieldColor;
        textBox.ForeColor = TextColor;
    }

    private static void ConfigureNumber(
        NumericUpDown control,
        decimal minimum,
        decimal maximum,
        decimal increment,
        int decimalPlaces)
    {
        control.Minimum = minimum;
        control.Maximum = maximum;
        control.Increment = increment;
        control.DecimalPlaces = decimalPlaces;
        control.BackColor = FieldColor;
        control.ForeColor = TextColor;
        control.BorderStyle = BorderStyle.FixedSingle;
        control.Margin = new Padding(0, 1, 5, 0);
    }

    private static Button CreateSmallButton(string text, EventHandler click)
    {
        var button = new Button
        {
            Dock = DockStyle.Fill,
            Text = text,
            Margin = new Padding(4, 3, 0, 3),
            FlatStyle = FlatStyle.Flat,
            BackColor = FieldColor,
            ForeColor = TextColor,
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += click;
        return button;
    }

    private static void ConfigureButton(
        Button button,
        string text,
        Color background,
        Color foreground,
        int width)
    {
        button.Text = text;
        button.Width = width;
        button.Height = 40;
        button.Margin = new Padding(0, 0, 9, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = background;
        button.ForeColor = foreground;
        button.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
    }

    private static void SetNumericValue(NumericUpDown control, decimal value)
    {
        control.Value = Math.Clamp(value, control.Minimum, control.Maximum);
    }

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }
}
