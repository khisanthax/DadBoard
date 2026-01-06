using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace DadBoard.App;

sealed class InstallProgressForm : Form
{
    private readonly InstallSession _session;
    private readonly bool _addFirewall;
    private readonly Dictionary<string, ListViewItem> _stepItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly System.Windows.Forms.Timer _autoCloseTimer = new();
    private readonly int _autoCloseSeconds = 5;

    private readonly Label _titleLabel = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _summaryLabel = new();
    private readonly ListView _stepsView = new();
    private readonly TextBox _logBox = new();
    private readonly Button _launchButton = new();
    private readonly Button _openLogButton = new();
    private readonly Button _copyErrorButton = new();
    private readonly Button _closeButton = new();

    private Process? _installProcess;
    private InstallStatusSnapshot _snapshot;
    private bool _handledExit;
    private bool _installFinished;
    private string? _failureMessage;
    private bool _userInteracted;
    private DateTime? _autoCloseDeadline;
    private bool _finalLogged;
    private bool _waitingForTrayReady;

    public InstallProgressForm(bool addFirewall)
    {
        _addFirewall = addFirewall;
        _session = Installer.CreateInstallSession();
        _snapshot = InstallStatusFactory.CreateDefault();

        Text = "DadBoard Installer";
        Size = new Size(780, 640);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        _titleLabel.Text = "Installing DadBoard...";
        _titleLabel.AutoSize = true;
        _titleLabel.Font = new Font(_titleLabel.Font, FontStyle.Bold);

        _progress.Dock = DockStyle.Fill;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 30;

        _summaryLabel.AutoSize = true;
        _summaryLabel.Text = "";

        ConfigureStepsView();
        ConfigureLogBox();
        ConfigureButtons();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(12)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(_titleLabel, 0, 0);
        layout.Controls.Add(_progress, 0, 1);
        layout.Controls.Add(_summaryLabel, 0, 2);
        layout.Controls.Add(_stepsView, 0, 3);
        layout.Controls.Add(_logBox, 0, 4);
        layout.Controls.Add(CreateButtonsPanel(), 0, 5);

        Controls.Add(layout);

        _timer.Interval = 500;
        _timer.Tick += (_, _) => RefreshStatus();

        _autoCloseTimer.Interval = 250;
        _autoCloseTimer.Tick += (_, _) => AutoCloseTick();

        WireInteractionHandlers(this);
    }

    public bool InstallSucceeded => _installFinished && _snapshot.Success;
    public bool LaunchedInstalledCopy { get; private set; }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        StartInstall();
    }

    private void ConfigureStepsView()
    {
        _stepsView.Dock = DockStyle.Fill;
        _stepsView.View = View.Details;
        _stepsView.FullRowSelect = true;
        _stepsView.GridLines = true;
        _stepsView.Columns.Add("Step", 300);
        _stepsView.Columns.Add("Status", 120);
        _stepsView.Columns.Add("Message", 300);

        foreach (var step in InstallSteps.Ordered)
        {
            var item = new ListViewItem(new[] { step, InstallStepStatus.Pending.ToString(), "" });
            _stepsView.Items.Add(item);
            _stepItems[step] = item;
        }
    }

    private void ConfigureLogBox()
    {
        _logBox.Dock = DockStyle.Fill;
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Font = new Font(FontFamily.GenericMonospace, 9f);
    }

    private void ConfigureButtons()
    {
        _launchButton.Text = "Open DadBoard";
        _launchButton.Enabled = false;
        _launchButton.Click += (_, _) =>
        {
            MarkUserInteraction();
            OpenInstalledCopy();
        };

        _openLogButton.Text = "Open log folder";
        _openLogButton.Enabled = false;
        _openLogButton.Click += (_, _) =>
        {
            MarkUserInteraction();
            OpenLogFolder();
        };

        _copyErrorButton.Text = "Copy error details";
        _copyErrorButton.Enabled = false;
        _copyErrorButton.Click += (_, _) =>
        {
            MarkUserInteraction();
            CopyErrorDetails();
        };

        _closeButton.Text = "Close";
        _closeButton.Click += (_, _) =>
        {
            MarkUserInteraction();
            Close();
        };
    }

    private Control CreateButtonsPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };

        panel.Controls.Add(_closeButton);
        panel.Controls.Add(_copyErrorButton);
        panel.Controls.Add(_openLogButton);
        panel.Controls.Add(_launchButton);
        return panel;
    }

    private void StartInstall()
    {
        try
        {
            _snapshot = InstallStatusFactory.CreateDefault();
            _snapshot.GetOrAddStep(InstallSteps.Elevate).Status = InstallStepStatus.Running;
            InstallStatusIo.Write(_session.StatusPath, _snapshot);
            UpdateSteps(_snapshot);
            TryLog("Install started.");
            SingleInstanceManager.SignalShutdown();

            _installProcess = Installer.StartElevatedInstall(_session, _addFirewall);
            if (_installProcess == null)
            {
                MarkFailure("Failed to start elevated installer.");
                return;
            }

            _timer.Start();
        }
        catch (Exception ex)
        {
            MarkFailure($"Failed to start installer: {ex.Message}");
        }
    }

    private void RefreshStatus()
    {
        ReadSnapshot();
        UpdateSteps(_snapshot);
        UpdateLog();

        if (_installProcess != null && _installProcess.HasExited && !_handledExit)
        {
            _handledExit = true;
            HandleProcessExit();
        }

        if (_snapshot.Completed && !_installFinished)
        {
            _installFinished = true;
            _timer.Stop();
            UpdateFinalUi();
        }
    }

    private void ReadSnapshot()
    {
        var snapshot = InstallStatusIo.Read(_session.StatusPath);
        if (snapshot != null)
        {
            _snapshot = InstallStatusFactory.EnsureSteps(snapshot);
        }
    }

    private void UpdateSteps(InstallStatusSnapshot snapshot)
    {
        foreach (var step in snapshot.Steps)
        {
            if (!_stepItems.TryGetValue(step.Name, out var item))
            {
                item = new ListViewItem(new[] { step.Name, step.Status.ToString(), step.Message ?? "" });
                _stepsView.Items.Add(item);
                _stepItems[step.Name] = item;
            }

            item.SubItems[1].Text = step.Status.ToString();
            item.SubItems[2].Text = step.Message ?? "";
        }
    }

    private void UpdateLog()
    {
        try
        {
            if (!File.Exists(_session.LogPath))
            {
                return;
            }

            var text = ReadLogTail(_session.LogPath, 200);
            if (!string.Equals(_logBox.Text, text, StringComparison.Ordinal))
            {
                _logBox.Text = text;
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.ScrollToCaret();
            }
        }
        catch
        {
        }
    }

    private static string ReadLogTail(string path, int maxLines)
    {
        var lines = new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine() ?? "";
            lines.Add(line);
            if (lines.Count > maxLines)
            {
                lines.RemoveAt(0);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void HandleProcessExit()
    {
        if (_snapshot.Completed)
        {
            return;
        }

        if (_snapshot.Steps.Any(s => s.Status == InstallStepStatus.Failed))
        {
            MarkFailure(_snapshot.ErrorMessage ?? "Install failed.");
            return;
        }

        if (!RequiredStepsSucceeded())
        {
            MarkFailure("Installer exited early. Open log?");
            return;
        }

        if (!WaitForTrayReady())
        {
            return;
        }

        SetSuccessState();
    }

    private bool WaitForTrayReady()
    {
        SetWaitingForTray(true);
        var step = _snapshot.GetOrAddStep(InstallSteps.Launch);
        step.Status = InstallStepStatus.Running;
        step.Message = "Waiting for tray readiness.";
        InstallStatusIo.Write(_session.StatusPath, _snapshot);
        TryLog("Waiting for tray ready signal.");

        if (!InstallHandoff.WaitForTrayReady(_session.Id, TimeSpan.FromSeconds(10)))
        {
            return FailLaunch("Installed tray did not confirm readiness (timeout).");
        }

        step.Status = InstallStepStatus.Success;
        step.Message = "Tray ready confirmed.";
        InstallStatusIo.Write(_session.StatusPath, _snapshot);
        TryLog("Tray ready confirmed.");
        LaunchedInstalledCopy = true;
        return true;
    }

    private bool FailLaunch(string message)
    {
        SetWaitingForTray(false);
        var agentLine = TryGetLastAgentLogLine();
        if (!string.IsNullOrWhiteSpace(agentLine))
        {
            message = $"{message} Last agent.log: {agentLine}";
        }

        var step = _snapshot.GetOrAddStep(InstallSteps.Launch);
        step.Status = InstallStepStatus.Failed;
        step.Message = message;
        _snapshot.Completed = true;
        _snapshot.Success = false;
        _snapshot.ErrorMessage = message;
        InstallStatusIo.Write(_session.StatusPath, _snapshot);
        TryLog($"Launch failed: {message}");
        return false;
    }

    private void MarkFailure(string message)
    {
        SetWaitingForTray(false);
        _failureMessage = message;
        _snapshot.Completed = true;
        _snapshot.Success = false;
        _snapshot.ErrorMessage = message;
        InstallStatusIo.Write(_session.StatusPath, _snapshot);
        TryLog($"Install failed: {message}");
        _timer.Stop();
        UpdateFinalUi();
    }

    private bool RequiredStepsSucceeded()
    {
        return GetStepStatus(InstallSteps.CopyExe) == InstallStepStatus.Success &&
               GetStepStatus(InstallSteps.CreateData) == InstallStepStatus.Success &&
               GetStepStatus(InstallSteps.CreateTask) == InstallStepStatus.Success &&
               GetStepStatus(InstallSteps.Firewall) == InstallStepStatus.Success;
    }

    private InstallStepStatus GetStepStatus(string stepName)
    {
        var step = _snapshot.Steps.FirstOrDefault(s => string.Equals(s.Name, stepName, StringComparison.OrdinalIgnoreCase));
        return step?.Status ?? InstallStepStatus.Pending;
    }

    private void TryLog(string message)
    {
        try
        {
            var logger = new InstallLogger(_session.LogPath);
            logger.Info(message);
        }
        catch
        {
        }
    }

    private void UpdateFinalUi()
    {
        if (_snapshot.Success)
        {
            _titleLabel.Text = "✅ Install complete";
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 100;
            _launchButton.Enabled = true;
            _openLogButton.Enabled = true;
            _copyErrorButton.Enabled = false;
            _summaryLabel.Text = BuildSuccessSummary();
            StartAutoClose();
        }
        else
        {
            _titleLabel.Text = "Install failed.";
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
            _copyErrorButton.Enabled = true;
            _openLogButton.Enabled = true;
            _summaryLabel.Text = _snapshot.ErrorMessage ?? "Install did not complete.";
        }

        UpdateLog();
    }

    private void OpenLogFolder()
    {
        try
        {
            var folder = Path.GetDirectoryName(_session.LogPath);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                MessageBox.Show("Log folder not found yet.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void CopyErrorDetails()
    {
        var error = _snapshot.ErrorMessage ?? _failureMessage ?? "Unknown error.";
        var details = $"{_titleLabel.Text}{Environment.NewLine}{error}";
        try
        {
            Clipboard.SetText(details);
        }
        catch
        {
        }
    }

    private void SetSuccessState()
    {
        SetWaitingForTray(false);
        if (!_snapshot.Completed)
        {
            _snapshot.Completed = true;
            _snapshot.Success = true;
            _snapshot.ErrorMessage = null;
            InstallStatusIo.Write(_session.StatusPath, _snapshot);
        }

        if (!_installFinished)
        {
            _installFinished = true;
            _timer.Stop();
        }

        UpdateFinalUi();
    }

    private void StartAutoClose()
    {
        if (_userInteracted)
        {
            return;
        }

        _autoCloseDeadline = DateTime.UtcNow.AddSeconds(_autoCloseSeconds);
        _autoCloseTimer.Start();
    }

    private void SetWaitingForTray(bool waiting)
    {
        _waitingForTrayReady = waiting;
        _closeButton.Enabled = !waiting;
        if (waiting)
        {
            _summaryLabel.Text = "Finishing setup... please wait.";
        }
    }

    private void AutoCloseTick()
    {
        if (_userInteracted)
        {
            _autoCloseTimer.Stop();
            return;
        }

        if (_autoCloseDeadline.HasValue && DateTime.UtcNow >= _autoCloseDeadline.Value)
        {
            _autoCloseTimer.Stop();
            LogFinalIfNeeded();
            Close();
        }
    }

    private void MarkUserInteraction()
    {
        _userInteracted = true;
        if (_autoCloseTimer.Enabled)
        {
            _autoCloseTimer.Stop();
        }
    }

    private void WireInteractionHandlers(Control control)
    {
        control.MouseDown += (_, _) => MarkUserInteraction();
        control.KeyDown += (_, _) => MarkUserInteraction();

        foreach (Control child in control.Controls)
        {
            WireInteractionHandlers(child);
        }
    }

    private void LogFinalIfNeeded()
    {
        if (_finalLogged || !_snapshot.Success)
        {
            return;
        }

        _finalLogged = true;
        TryLog("Install complete (UI confirmed).");
    }

    private string BuildSuccessSummary()
    {
        var lines = _snapshot.Steps
            .Select(step => $"{step.Name}: {step.Status}")
            .ToArray();
        return $"Completed steps: {string.Join(" | ", lines)}";
    }

    private void OpenInstalledCopy()
    {
        try
        {
            var installedExe = Installer.GetInstalledExePath();
            if (!File.Exists(installedExe))
            {
                MessageBox.Show("Installed DadBoard.exe not found.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Process.Start(new ProcessStartInfo(installedExe) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_waitingForTrayReady)
        {
            _summaryLabel.Text = "Finishing setup... please wait.";
            e.Cancel = true;
            return;
        }

        LogFinalIfNeeded();
        base.OnFormClosing(e);
    }

    private string? TryGetLastAgentLogLine()
    {
        try
        {
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DadBoard");
            var logPath = Path.Combine(baseDir, "logs", "agent.log");
            if (!File.Exists(logPath))
            {
                return null;
            }

            return ReadLastNonEmptyLine(logPath);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadLastNonEmptyLine(string path)
    {
        string? last = null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (!string.IsNullOrWhiteSpace(line))
            {
                last = line;
            }
        }

        return last;
    }
}
