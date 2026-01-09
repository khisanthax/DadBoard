using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DadBoard.Spine.Shared;

namespace DadBoard.Setup;

public sealed class SetupForm : Form
{
    private readonly Label _statusLabel = new();
    private readonly TextBox _logBox = new();
    private readonly Button _installButton = new();
    private readonly Button _updateButton = new();
    private readonly Button _uninstallButton = new();
    private readonly Button _startNowButton = new();
    private readonly Button _startButton = new();
    private readonly Button _closeButton = new();
    private readonly FlowLayoutPanel _actionsPanel = new();
    private readonly FlowLayoutPanel _finalPanel = new();
    private SetupLogger? _logger;
    private readonly CancellationTokenSource _cts = new();

    private bool _busy;

    public SetupForm()
    {
        Text = "DadBoard Setup";
        Size = new Size(720, 520);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new Label
        {
            Text = "DadBoard Setup",
            Font = new Font(Font.FontFamily, 12, FontStyle.Bold),
            AutoSize = true
        };

        _statusLabel.Text = "Ready.";
        _statusLabel.AutoSize = true;

        _actionsPanel.Dock = DockStyle.Fill;
        _actionsPanel.AutoSize = true;
        _actionsPanel.FlowDirection = FlowDirection.LeftToRight;

        _installButton.Text = "Install";
        _installButton.AutoSize = true;
        _installButton.Click += async (_, _) => await RunActionAsync(SetupAction.Install);

        _updateButton.Text = "Update";
        _updateButton.AutoSize = true;
        _updateButton.Click += async (_, _) => await RunActionAsync(SetupAction.Update);

        _uninstallButton.Text = "Uninstall";
        _uninstallButton.AutoSize = true;
        _uninstallButton.Click += async (_, _) => await RunActionAsync(SetupAction.Uninstall);

        _startNowButton.Text = "Start DadBoard";
        _startNowButton.AutoSize = true;
        _startNowButton.Enabled = false;
        _startNowButton.Click += (_, _) => LaunchInstalledApp();

        _actionsPanel.Controls.AddRange(new Control[]
        {
            _installButton,
            _updateButton,
            _uninstallButton,
            _startNowButton
        });

        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Dock = DockStyle.Fill;

        _finalPanel.Dock = DockStyle.Fill;
        _finalPanel.AutoSize = true;
        _finalPanel.FlowDirection = FlowDirection.LeftToRight;
        _finalPanel.Visible = false;

        _startButton.Text = "Start DadBoard";
        _startButton.AutoSize = true;
        _startButton.Enabled = false;
        _startButton.Click += (_, _) => LaunchInstalledApp();

        _closeButton.Text = "Close";
        _closeButton.AutoSize = true;
        _closeButton.Click += (_, _) => Close();

        _finalPanel.Controls.Add(_startButton);
        _finalPanel.Controls.Add(_closeButton);

        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_statusLabel, 0, 1);
        layout.Controls.Add(_actionsPanel, 0, 2);
        layout.Controls.Add(_logBox, 0, 3);
        layout.Controls.Add(_finalPanel, 0, 4);
        Controls.Add(layout);

        FormClosed += (_, _) => _cts.Cancel();

        try
        {
            _logger = new SetupLogger();
            AppendLog($"Log file: {_logger.LogPath}");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Logging failed: {ex.Message}";
            MessageBox.Show(
                $"Setup cannot open log file.{Environment.NewLine}{ex.Message}",
                "DadBoard Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            TryFallbackLogger();
        }

        ShowActionsView("startup");
        UpdateActionButtons();
    }

    private async Task RunActionAsync(SetupAction action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateActionButtons();
        _startButton.Enabled = false;

        if (_logger == null)
        {
            MessageBox.Show("Logging is not available. Setup cannot continue.", "DadBoard Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _busy = false;
            UpdateActionButtons();
            return;
        }

        var progress = new Progress<string>(message =>
        {
            _statusLabel.Text = message;
            AppendLog(message);
        });

        var manifestUrl = UpdateConfigStore.Load().ManifestUrl;
        var result = await SetupOperations.RunAsync(action, manifestUrl, _logger, progress, _cts.Token);

        if (result.Success)
        {
            _statusLabel.Text = $"{action} complete.";
            AppendLog($"{action} complete.");
            if (action == SetupAction.Uninstall)
            {
                _startButton.Enabled = false;
                ShowActionsView("uninstall_complete");
            }
            else
            {
                _startButton.Enabled = true;
                ShowFinalView("install_complete");
            }
        }
        else
        {
            _statusLabel.Text = $"{action} failed: {result.Error}";
            AppendLog($"{action} failed: {result.Error}");
            MessageBox.Show(result.Error ?? "Setup failed.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        _busy = false;
        UpdateActionButtons();
    }

    private void UpdateActionButtons()
    {
        var canonicalExists = File.Exists(DadBoardPaths.InstalledExePath);
        var legacyExists = File.Exists(DadBoardPaths.LegacyInstallExePath);
        var canonicalRunnable = IsCanonicalRunnable(out var canonicalReason);
        var updateSourceConfigured = IsUpdateSourceConfigured();

        var installEnabled = !_busy && !canonicalRunnable;
        var updateEnabled = !_busy && canonicalRunnable && updateSourceConfigured;
        var uninstallEnabled = !_busy && canonicalRunnable;
        var startEnabled = canonicalRunnable;

        _installButton.Enabled = installEnabled;
        _updateButton.Enabled = updateEnabled;
        _uninstallButton.Enabled = uninstallEnabled;
        _startButton.Enabled = startEnabled;
        _startNowButton.Enabled = startEnabled;

        if (!canonicalRunnable && (Directory.Exists(DadBoardPaths.InstallDir) || legacyExists))
        {
            _installButton.Text = "Repair";
        }
        else
        {
            _installButton.Text = "Install";
        }

        var installReason = installEnabled
            ? "needs_install_or_repair"
            : canonicalRunnable ? "installed_and_runnable" : "busy";

        var startReason = startEnabled ? "runnable" : canonicalReason;

        _logger?.Info($"canonical_exists={canonicalExists} legacy_exists={legacyExists} initial_view={( _finalPanel.Visible ? "Final" : "Actions")} reason=update_buttons");
        _logger?.Info($"install_enabled={installEnabled} reason={installReason}");
        _logger?.Info($"start_enabled={startEnabled} reason={startReason}");
    }

    private void ShowActionsView(string reason)
    {
        _actionsPanel.Visible = true;
        _finalPanel.Visible = false;
        LogInstallDetection(reason, "Actions");
    }

    private void ShowFinalView(string reason)
    {
        _actionsPanel.Visible = false;
        _finalPanel.Visible = true;
        LogInstallDetection(reason, "Final");
    }

    private void LogInstallDetection(string reason, string initialView)
    {
        var canonicalExists = File.Exists(DadBoardPaths.InstalledExePath);
        var legacyExists = File.Exists(DadBoardPaths.LegacyInstallExePath);
        if (legacyExists && !canonicalExists)
        {
            _statusLabel.Text =
                "Legacy install detected at Program Files; new install will migrate to user-writable location.";
        }

        _logger?.Info($"canonical_exists={canonicalExists} legacy_exists={legacyExists} initial_view={initialView} reason={reason}");
        AppendLog($"canonical_exists={canonicalExists} legacy_exists={legacyExists} initial_view={initialView} reason={reason}");
    }

    private void LaunchInstalledApp()
    {
        if (!File.Exists(DadBoardPaths.InstalledExePath))
        {
            MessageBox.Show("DadBoard.exe not found.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo(DadBoardPaths.InstalledExePath) { UseShellExecute = true });
    }

    private void AppendLog(string message)
    {
        _logBox.AppendText($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
    }

    private bool IsCanonicalRunnable(out string reason)
    {
        reason = "missing_exe";
        try
        {
            if (!File.Exists(DadBoardPaths.InstalledExePath))
            {
                return false;
            }

            var info = new FileInfo(DadBoardPaths.InstalledExePath);
            if (info.Length <= 0)
            {
                reason = "empty_file";
                return false;
            }

            FileVersionInfo.GetVersionInfo(DadBoardPaths.InstalledExePath);
            reason = "runnable";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"invalid_exe:{ex.Message}";
            return false;
        }
    }

    private bool IsUpdateSourceConfigured()
    {
        var config = UpdateConfigStore.Load();
        if (!string.IsNullOrWhiteSpace(config.ManifestUrl))
        {
            return true;
        }

        var localManifest = Path.Combine(DadBoardPaths.UpdateSourceDir, "latest.json");
        return File.Exists(localManifest);
    }

    private void TryFallbackLogger()
    {
        try
        {
            var fallbackDir = Path.Combine(Path.GetTempPath(), "DadBoardSetup");
            Directory.CreateDirectory(fallbackDir);
            var fallbackPath = Path.Combine(fallbackDir, "setup.log");
            _logger = new SetupLogger(fallbackPath);
            AppendLog($"Log file (fallback): {_logger.LogPath}");
            _statusLabel.Text = "Logging fallback enabled. Setup can continue.";
        }
        catch (Exception ex)
        {
            _logger = null;
            _statusLabel.Text = $"Logging failed: {ex.Message}";
            MessageBox.Show(
                $"Setup cannot open any log file.{Environment.NewLine}{ex.Message}",
                "DadBoard Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _logger?.Dispose();
        base.OnFormClosed(e);
    }
}
