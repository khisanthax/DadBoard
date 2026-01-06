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

    private readonly Label _titleLabel = new();
    private readonly ProgressBar _progress = new();
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
    private bool _exitLogged;

    public InstallProgressForm(bool addFirewall)
    {
        _addFirewall = addFirewall;
        _session = Installer.CreateInstallSession();
        _snapshot = InstallStatusFactory.CreateDefault();

        Text = "DadBoard Installer";
        Size = new Size(780, 620);
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

        ConfigureStepsView();
        ConfigureLogBox();
        ConfigureButtons();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(12)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(_titleLabel, 0, 0);
        layout.Controls.Add(_progress, 0, 1);
        layout.Controls.Add(_stepsView, 0, 2);
        layout.Controls.Add(_logBox, 0, 3);
        layout.Controls.Add(CreateButtonsPanel(), 0, 4);

        Controls.Add(layout);

        _timer.Interval = 500;
        _timer.Tick += (_, _) => RefreshStatus();
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
        _launchButton.Text = "Launch DadBoard";
        _launchButton.Enabled = false;
        _launchButton.Click += (_, _) => LaunchInstalledCopy(force: true);

        _openLogButton.Text = "Open install log";
        _openLogButton.Enabled = true;
        _openLogButton.Click += (_, _) => OpenLog();

        _copyErrorButton.Text = "Copy error details";
        _copyErrorButton.Enabled = false;
        _copyErrorButton.Click += (_, _) => CopyErrorDetails();

        _closeButton.Text = "Close";
        _closeButton.Click += (_, _) => Close();
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

        if (!LaunchInstalledCopy(force: false))
        {
            return;
        }

        _snapshot.Completed = true;
        _snapshot.Success = true;
        _snapshot.ErrorMessage = null;
        InstallStatusIo.Write(_session.StatusPath, _snapshot);
        CloseAfterSuccess();
    }

    private bool LaunchInstalledCopy(bool force)
    {
        if (!force && _snapshot.Completed)
        {
            return false;
        }

        var step = _snapshot.GetOrAddStep(InstallSteps.Launch);
        step.Status = InstallStepStatus.Running;
        step.Message = "Launching installed copy.";
        InstallStatusIo.Write(_session.StatusPath, _snapshot);
        TryLog("Launching installed copy.");

        try
        {
            var installedExe = Installer.GetInstalledExePath();
            if (!File.Exists(installedExe))
            {
                return FailLaunch("Installed DadBoard.exe not found.");
            }

            var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(currentExe) &&
                string.Equals(currentExe, installedExe, StringComparison.OrdinalIgnoreCase))
            {
                step.Status = InstallStepStatus.Success;
                step.Message = "Already running.";
                InstallStatusIo.Write(_session.StatusPath, _snapshot);
                LaunchedInstalledCopy = false;
                return true;
            }

            var arguments = $"--postinstall {_session.Id}";
            var proc = Process.Start(new ProcessStartInfo(installedExe, arguments) { UseShellExecute = true });
            if (proc == null)
            {
                return FailLaunch("Failed to launch installed copy.");
            }

            System.Threading.Thread.Sleep(500);
            var exitedEarly = proc.HasExited;
            if (exitedEarly)
            {
                TryLog("Installed copy exited early; waiting for ready signal.");
            }

            TryLog("Launched installed copy.");

            if (!InstallHandoff.WaitForReady(_session.Id, TimeSpan.FromSeconds(10)))
            {
                var reason = exitedEarly
                    ? "Installed copy exited early and did not confirm readiness (timeout)."
                    : "Installed copy did not confirm readiness (timeout).";
                return FailLaunch(reason);
            }

            step.Status = InstallStepStatus.Success;
            step.Message = "Installed copy confirmed.";
            InstallStatusIo.Write(_session.StatusPath, _snapshot);
            TryLog("Installed copy confirmed.");
            LaunchedInstalledCopy = true;
            if (!_snapshot.Completed)
            {
                _snapshot.Completed = true;
                _snapshot.Success = true;
                _snapshot.ErrorMessage = null;
                InstallStatusIo.Write(_session.StatusPath, _snapshot);
            }
            CloseAfterSuccess();
            return true;
        }
        catch (Exception ex)
        {
            return FailLaunch(ex.Message);
        }
    }

    private bool FailLaunch(string message)
    {
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
            _titleLabel.Text = "Install complete.";
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 100;
            _launchButton.Enabled = true;
            _copyErrorButton.Enabled = false;
        }
        else
        {
            _titleLabel.Text = "Install failed.";
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
            _copyErrorButton.Enabled = true;
        }

        UpdateLog();
        if (_snapshot.Success)
        {
            CloseAfterSuccess();
        }
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(_session.LogPath))
            {
                MessageBox.Show("Install log not found yet.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Process.Start(new ProcessStartInfo(_session.LogPath) { UseShellExecute = true });
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

    private void CloseAfterSuccess()
    {
        if (_exitLogged)
        {
            Close();
            return;
        }

        _exitLogged = true;
        TryLog("Installer exiting.");
        Close();
    }
}
