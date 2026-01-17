using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using DadBoard.Agent;
using DadBoard.Leader;
using DadBoard.Spine.Shared;

namespace DadBoard.App;

sealed class TrayAppContext : ApplicationContext
{
    private readonly string _baseDir;
    private readonly string _agentConfigPath;
    private readonly AppConfigStore _configStore;
    private readonly SynchronizationContext _uiContext;
    private readonly string? _postInstallId;
    private System.Windows.Forms.Timer? _postInstallTimer;

    private readonly AgentService _agent;
    private LeaderService? _leader;
    private LeaderForm? _leaderForm;
    private StatusForm? _statusForm;
    private DiagnosticsForm? _diagnosticsForm;

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _enableLeaderItem;
    private readonly ToolStripMenuItem _disableLeaderItem;
    private readonly ToolStripMenuItem _openDashboardItem;
    private readonly ToolStripMenuItem _startLeaderOnLoginItem;
    private readonly ToolStripMenuItem _installItem;
    private readonly ToolStripMenuItem _checkUpdatesItem;
    private readonly ToolStripMenuItem _viewUpdateStatusItem;
    private readonly ToolStripMenuItem _repairItem;
    private readonly ToolStripMenuItem _resetUpdateFailuresItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _diagnosticsItem;

    private readonly AppLaunchOptions _options;
    private AgentConfig _config;
    private bool _disposed;
    private readonly UpdateLogger _updateLogger = new();
    private readonly AppLogger _appLogger;
    private int _exitRequested;

    public TrayAppContext(AppLaunchOptions options, AppLogger appLogger)
    {
        _options = options;
        _appLogger = appLogger;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _postInstallId = options.PostInstallId;
        _baseDir = DataPaths.ResolveBaseDir();
        _agentConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DadBoard",
            "Agent",
            "agent.config.json");
        _configStore = new AppConfigStore(_agentConfigPath);

        _agent = new AgentService(_baseDir);
        _agent.ShutdownRequested += () =>
        {
            _appLogger.Info("Shutdown requested by agent.");
            ShowTrayNotice("DadBoard is closing (update or remote command).", ToolTipIcon.Info);
            _uiContext.Post(_ => Exit(), null);
        };
        _agent.Start();
        _appLogger.Info("TrayAppContext started.");

        _config = _configStore.Load();

        _enableLeaderItem = new ToolStripMenuItem("Enable Leader", null, (_, _) => EnableLeader(showUI: true));
        _disableLeaderItem = new ToolStripMenuItem("Disable Leader", null, (_, _) => DisableLeader());
        _openDashboardItem = new ToolStripMenuItem("Open Dashboard", null, (_, _) => ShowLeaderUI());
        _startLeaderOnLoginItem = new ToolStripMenuItem("Start Leader on Login") { CheckOnClick = true };
        _startLeaderOnLoginItem.Checked = _config.StartLeaderOnLogin;
        _startLeaderOnLoginItem.CheckedChanged += (_, _) => ToggleStartLeaderOnLogin();

        _installItem = new ToolStripMenuItem("Install (Admin)", null, (_, _) => Install());
        _checkUpdatesItem = new ToolStripMenuItem("Check for updates now", null, (_, _) => _ = RunUpdaterAsync("check --interactive --auto"));
        _viewUpdateStatusItem = new ToolStripMenuItem("View update status/log", null, (_, _) => ShowUpdateStatus());
        _repairItem = new ToolStripMenuItem("Repair / Reinstall", null, (_, _) => _ = RunUpdaterAsync("repair --interactive --auto"));
        _resetUpdateFailuresItem = new ToolStripMenuItem("Reset Update Failures (This PC)", null, (_, _) => ResetUpdateFailuresLocal());
        _statusItem = new ToolStripMenuItem("Show Status", null, (_, _) => ShowStatus());
        _diagnosticsItem = new ToolStripMenuItem("Diagnostics", null, (_, _) => ShowDiagnostics());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _openDashboardItem,
            _enableLeaderItem,
            _disableLeaderItem,
            new ToolStripSeparator(),
            _startLeaderOnLoginItem,
            _installItem,
            _checkUpdatesItem,
            _viewUpdateStatusItem,
            _repairItem,
            _resetUpdateFailuresItem,
            _statusItem,
            _diagnosticsItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => Exit())
        });

        var trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        _tray = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "DadBoard",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) =>
        {
            if (_leader != null)
            {
                ShowLeaderUI();
            }
            else
            {
                ShowStatus();
            }
        };
        SignalTrayReadyWhenReady();

        if (_options.Mode == AppMode.Leader || (_options.Mode != AppMode.Agent && _config.StartLeaderOnLogin))
        {
            EnableLeader(showUI: _options.Mode == AppMode.Leader);
        }

        UpdateMenuState();
    }

    private void EnableLeader(bool showUI)
    {
        if (_leader != null)
        {
            if (showUI)
            {
                ShowLeaderUI();
            }
            return;
        }

        _leader = new LeaderService(_baseDir);
        _appLogger.Info("Leader enabled.");
        if (showUI)
        {
            ShowLeaderUI();
        }

        UpdateMenuState();
    }

    private void DisableLeader()
    {
        if (_leader == null)
        {
            return;
        }

        _leaderForm?.AllowClose();
        _leaderForm?.Close();
        _leaderForm = null;

        _leader.Dispose();
        _leader = null;
        _appLogger.Info("Leader disabled.");

        _statusForm?.UpdateLeader(null);
        UpdateMenuState();
    }

    private void ShowLeaderUI()
    {
        if (_leader == null)
        {
            return;
        }

        if (_leaderForm == null || _leaderForm.IsDisposed)
        {
            _leaderForm = new LeaderForm(_leader);
            _leaderForm.FormClosed += (_, _) => { _leaderForm = null; };
        }

        _leaderForm.Show();
        _leaderForm.BringToFront();
    }

    private void ShowStatus()
    {
        if (_statusForm == null || _statusForm.IsDisposed)
        {
            _statusForm = new StatusForm();
        }

        _statusForm.UpdateLeader(_leader);
        _statusForm.Show();
        _statusForm.BringToFront();
    }

    private void ShowDiagnostics()
    {
        if (_diagnosticsForm == null || _diagnosticsForm.IsDisposed)
        {
            _diagnosticsForm = new DiagnosticsForm(_leader);
            _diagnosticsForm.FormClosed += (_, _) => _diagnosticsForm = null;
        }
        else
        {
            _diagnosticsForm.UpdateSnapshot(_leader);
        }

        _diagnosticsForm.Show();
        _diagnosticsForm.BringToFront();
    }

    public void HandleActivateSignal()
    {
        if (_options.StartMinimized || _options.Mode == AppMode.Agent)
        {
            return;
        }

        _uiContext.Post(_ => ShowStatus(), null);
    }

    public void HandleShutdownSignal()
    {
        _uiContext.Post(_ => Exit(), null);
    }

    private void SignalTrayReadyWhenReady()
    {
        if (string.IsNullOrWhiteSpace(_postInstallId))
        {
            return;
        }

        _postInstallTimer = new System.Windows.Forms.Timer
        {
            Interval = 250
        };
        _postInstallTimer.Tick += (_, _) =>
        {
            _postInstallTimer?.Stop();
            _postInstallTimer?.Dispose();
            _postInstallTimer = null;
            InstallHandoff.SignalTrayReady(_postInstallId);
        };
        _postInstallTimer.Start();
    }

    private void ToggleStartLeaderOnLogin()
    {
        _config.StartLeaderOnLogin = _startLeaderOnLoginItem.Checked;
        _configStore.Save(_config);
    }

    private void Install()
    {
        var result = MessageBox.Show(
            "Install DadBoard for this PC? This requires admin approval.",
            "DadBoard",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);

        if (result != DialogResult.OK)
        {
            return;
        }

        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
        {
            MessageBox.Show("Unable to start installer.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var startInfo = new ProcessStartInfo(exePath, "--install")
        {
            UseShellExecute = true
        };

        var proc = Process.Start(startInfo);
        if (proc == null)
        {
            MessageBox.Show("Failed to launch installer.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Exit();
    }

    private void ResetUpdateFailuresLocal()
    {
        var confirm = MessageBox.Show(
            "This clears the update circuit breaker and retry backoff for this PC. Continue?",
            "DadBoard",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        _agent.ResetUpdateFailuresLocal("tray");
        MessageBox.Show(
            "Update failures reset. You can retry update now.",
            "DadBoard",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void UpdateMenuState()
    {
        var leaderEnabled = _leader != null;
        _enableLeaderItem.Enabled = !leaderEnabled;
        _disableLeaderItem.Enabled = leaderEnabled;
        _openDashboardItem.Enabled = leaderEnabled;
        var installed = Installer.IsInstalled();
        _installItem.Text = installed ? "Install (Admin)" : "Install (Admin)";
        _installItem.Enabled = !installed;

        var updaterExists = File.Exists(DadBoardPaths.UpdaterExePath);
        _checkUpdatesItem.Enabled = updaterExists;
        _repairItem.Enabled = updaterExists;
        _viewUpdateStatusItem.Enabled = true;
    }

    private System.Threading.Tasks.Task RunUpdaterAsync(string args)
    {
        var installDir = DadBoardPaths.InstallDir;
        var updaterPath = DadBoardPaths.UpdaterExePath;
        try
        {
            Directory.CreateDirectory(installDir);
            if (!File.Exists(updaterPath))
            {
                var result = MessageBox.Show(
                    $"DadBoardUpdater.exe not found at:{Environment.NewLine}{updaterPath}{Environment.NewLine}{Environment.NewLine}Run repair to restore updater?",
                    "DadBoard",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (result != DialogResult.Yes)
                {
                    return System.Threading.Tasks.Task.CompletedTask;
                }

                if (File.Exists(DadBoardPaths.SetupExePath))
                {
                    if (!TryLaunchExecutable("setup", DadBoardPaths.SetupExePath, "", installDir, out var setupError))
                    {
                        MessageBox.Show(
                            $"Failed to launch setup: {setupError}",
                            "DadBoard",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                    return System.Threading.Tasks.Task.CompletedTask;
                }

                return System.Threading.Tasks.Task.CompletedTask;
            }

            _updateLogger.Info($"Launching updater with args: {args}");
            if (!TryLaunchExecutable("updater", updaterPath, args, installDir, out var error))
            {
                MessageBox.Show(
                    $"Failed to launch updater: {error}",
                    "DadBoard",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            _updateLogger.Error($"RunUpdater failed: {ex}");
            MessageBox.Show(
                $"Failed to run updater: {ex.Message}",
                "DadBoard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        return System.Threading.Tasks.Task.CompletedTask;
    }

    private void ShowUpdateStatus()
    {
        var status = UpdaterStatusStore.Load();
        if (status == null)
        {
            MessageBox.Show("No updater status found yet.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var message =
            $"Action: {status.Action}{Environment.NewLine}" +
            $"Installed: {status.InstalledVersion}{Environment.NewLine}" +
            $"Available: {status.AvailableVersion}{Environment.NewLine}" +
            $"Result: {status.Result}{Environment.NewLine}" +
            $"Message: {status.Message}{Environment.NewLine}" +
            $"Manifest: {status.ManifestUrl}{Environment.NewLine}" +
            $"Last run: {status.TimestampUtc}{Environment.NewLine}" +
            $"Log: {status.LogPath}";

        using var dialog = new Form
        {
            Text = "DadBoard Update Status",
            Size = new Size(640, 360),
            StartPosition = FormStartPosition.CenterParent
        };

        var textBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            Text = message
        };

        var copyButton = new Button
        {
            Text = "Copy",
            AutoSize = true
        };
        copyButton.Click += (_, _) => Clipboard.SetText(message);

        var closeButton = new Button
        {
            Text = "Close",
            AutoSize = true
        };
        closeButton.Click += (_, _) => dialog.Close();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };
        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(copyButton);

        dialog.Controls.Add(textBox);
        dialog.Controls.Add(buttons);
        dialog.ShowDialog();
    }

    private bool TryLaunchExecutable(string label, string exePath, string args, string installDir, out string error)
    {
        error = "";
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                WorkingDirectory = installDir,
                UseShellExecute = true
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                error = "Process failed to start.";
                _updateLogger.Warn($"{label} start returned null for {exePath}");
                return false;
            }

            _updateLogger.Info($"Launched {label}: {exePath}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _updateLogger.Error($"{label} start failed: {ex}");
            return false;
        }
    }

    private void ShowTrayNotice(string message, ToolTipIcon icon)
    {
        if (!_tray.Visible)
        {
            return;
        }

        _tray.BalloonTipTitle = "DadBoard";
        _tray.BalloonTipText = message;
        _tray.BalloonTipIcon = icon;
        _tray.ShowBalloonTip(3000);
    }

    private void Exit()
    {
        // Tray exit is authoritative: stop services and terminate the process.
        if (Interlocked.Exchange(ref _exitRequested, 1) == 1)
        {
            return;
        }

        _appLogger.Info("TrayAppContext exit requested from tray.");
        _tray.Visible = false;
        _leaderForm?.AllowClose();
        _leaderForm?.Close();
        _statusForm?.AllowClose();
        _statusForm?.Close();
        _diagnosticsForm?.Close();
        _leader?.Dispose();
        _appLogger.Info("Stopping agent service.");
        _agent.Stop();
        Dispose();
        ExitThread();
        Application.Exit();
        _ = Task.Run(() =>
        {
            Thread.Sleep(3000);
            _appLogger.Warn("Force exiting after tray exit.");
            Environment.Exit(0);
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _postInstallTimer?.Stop();
            _postInstallTimer?.Dispose();
            _tray.Dispose();
            _leader?.Dispose();
            _agent.Dispose();
        }
        base.Dispose(disposing);
    }
}
