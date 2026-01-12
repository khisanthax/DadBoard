using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DadBoard.Spine.Shared;

namespace DadBoard.Updater;

sealed class UpdaterForm : Form
{
    private readonly Label _statusLabel = new();
    private readonly TextBox _logBox = new();
    private readonly Button _checkButton = new();
    private readonly Button _closeButton = new();
    private readonly UpdaterEngine _engine = new();
    private readonly UpdaterLogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _forceRepair;
    private readonly bool _autoRun;
    private bool _busy;

    public UpdaterForm(bool forceRepair, bool autoRun)
    {
        _forceRepair = forceRepair;
        _autoRun = autoRun;
        Text = "DadBoard Updater";
        Size = new Size(720, 480);
        StartPosition = FormStartPosition.CenterScreen;

        _logger = new UpdaterLogger();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _statusLabel.Text = _forceRepair ? "Ready to repair." : "Ready.";
        _statusLabel.AutoSize = true;

        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Dock = DockStyle.Fill;

        _checkButton.Text = _forceRepair ? "Run Repair Now" : "Check Nightly Now";
        _checkButton.AutoSize = true;
        _checkButton.Click += async (_, _) => await RunUpdateAsync();

        _closeButton.Text = "Close";
        _closeButton.AutoSize = true;
        _closeButton.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };
        buttons.Controls.Add(_closeButton);
        buttons.Controls.Add(_checkButton);

        layout.Controls.Add(_statusLabel, 0, 0);
        layout.Controls.Add(_logBox, 0, 1);
        layout.Controls.Add(buttons, 0, 2);

        Controls.Add(layout);

        if (_autoRun)
        {
            Shown += async (_, _) => await RunUpdateAsync();
        }
        FormClosed += (_, _) => _logger.Dispose();
    }

    private async Task RunUpdateAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _checkButton.Enabled = false;
        _statusLabel.Text = _forceRepair ? "Preparing repair..." : "Checking for updates...";
        AppendLog(_forceRepair ? "Starting repair." : "Starting update check.");

        try
        {
            var config = UpdateConfigStore.Load();
            var action = _forceRepair ? "repair" : "check";
            var result = await _engine.RunAsync(config, _forceRepair, action, _logger.LogPath, _cts.Token, AppendLog)
                .ConfigureAwait(true);

            switch (result.State)
            {
                case UpdaterState.UpToDate:
                    _statusLabel.Text = _forceRepair
                        ? $"Repair complete ({result.Version})."
                        : $"Up to date ({result.Version}).";
                    break;
                case UpdaterState.Updated:
                    _statusLabel.Text = $"Update applied ({result.Version}).";
                    break;
                default:
                    _statusLabel.Text = $"Update failed: {result.Message}";
                    break;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Updater error: {ex.Message}");
            _statusLabel.Text = "Update failed. See log.";
        }
        finally
        {
            _busy = false;
            _checkButton.Enabled = true;
        }
    }

    private void AppendLog(string message)
    {
        _logger.Info(message);
        AppendLogSafe(message);
    }

    private void AppendLogSafe(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(AppendLogSafe), message);
            return;
        }

        _logBox.AppendText($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
    }
}
