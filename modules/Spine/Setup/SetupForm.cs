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
    private readonly Button _startButton = new();
    private readonly Button _closeButton = new();
    private readonly SetupLogger _logger = new();
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
            RowCount = 4
        };
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

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight
        };

        _installButton.Text = "Install";
        _installButton.AutoSize = true;
        _installButton.Click += async (_, _) => await RunActionAsync(SetupAction.Install);

        _updateButton.Text = "Update";
        _updateButton.AutoSize = true;
        _updateButton.Click += async (_, _) => await RunActionAsync(SetupAction.Update);

        _uninstallButton.Text = "Uninstall";
        _uninstallButton.AutoSize = true;
        _uninstallButton.Click += async (_, _) => await RunActionAsync(SetupAction.Uninstall);

        actions.Controls.AddRange(new Control[]
        {
            _installButton,
            _updateButton,
            _uninstallButton
        });

        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Dock = DockStyle.Fill;

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight
        };

        _startButton.Text = "Start DadBoard";
        _startButton.AutoSize = true;
        _startButton.Enabled = false;
        _startButton.Click += (_, _) => LaunchInstalledApp();

        _closeButton.Text = "Close";
        _closeButton.AutoSize = true;
        _closeButton.Click += (_, _) => Close();

        footer.Controls.Add(_startButton);
        footer.Controls.Add(_closeButton);

        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_statusLabel, 0, 1);
        layout.Controls.Add(_logBox, 0, 2);
        layout.Controls.Add(footer, 0, 3);
        Controls.Add(layout);

        FormClosed += (_, _) => _cts.Cancel();

        UpdateActionButtons();
        AppendLog($"Log file: {_logger.LogPath}");
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
            _startButton.Enabled = action != SetupAction.Uninstall;
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
        var installed = File.Exists(DadBoardPaths.InstalledExePath);
        _installButton.Enabled = !_busy && !installed;
        _updateButton.Enabled = !_busy && installed;
        _uninstallButton.Enabled = !_busy && installed;
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
}
