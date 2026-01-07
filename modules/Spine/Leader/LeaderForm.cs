using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DadBoard.Spine.Shared;

namespace DadBoard.Leader;

public sealed class LeaderForm : Form
{
    private readonly LeaderService _service;
    private readonly ListBox _gameList = new();
    private readonly DataGridView _grid = new();
    private readonly Label _statusLabel = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly EventHandler _refreshHandler;
    private readonly Button _launchSelectedButton = new();
    private readonly Button _testButton = new();
    private readonly Button _testMissingButton = new();
    private readonly Button _shutdownButton = new();
    private readonly ContextMenuStrip _rowMenu = new();
    private readonly ToolStripMenuItem _menuLaunch = new("Launch on this PC");
    private readonly ToolStripMenuItem _menuTest = new("Test: Open Notepad");
    private readonly ToolStripMenuItem _menuCopyPcId = new("Copy PC ID");
    private readonly ToolStripMenuItem _menuCopyIp = new("Copy IP");
    private readonly ToolStripMenuItem _menuViewError = new("View last error");
    private int _refreshing;
    private bool _allowClose;

    public LeaderForm(LeaderService service)
    {
        _service = service;
        Text = "DadBoard Leader (Phase 1)";
        Size = new Size(980, 520);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(BuildLeftPanel(), 0, 0);
        layout.Controls.Add(BuildGrid(), 1, 0);
        layout.Controls.Add(_statusLabel, 0, 1);
        layout.SetColumnSpan(_statusLabel, 2);

        Controls.Add(layout);

        _refreshTimer.Interval = 1000;
        _refreshHandler = (_, _) => RefreshGridSafe();
        _refreshTimer.Tick += _refreshHandler;

        Shown += (_, _) => StartRefresh();
        VisibleChanged += (_, _) =>
        {
            if (Visible)
            {
                StartRefresh();
            }
            else
            {
                StopRefresh();
            }
        };
        FormClosing += OnFormClosing;
        FormClosed += OnFormClosed;
    }

    private Control BuildLeftPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 7,
            ColumnCount = 1
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        panel.Controls.Add(new Label { Text = "Games", AutoSize = true }, 0, 0);

        _gameList.Dock = DockStyle.Fill;
        _gameList.DisplayMember = "Name";
        foreach (var game in _service.GetGames())
        {
            _gameList.Items.Add(game);
        }
        _gameList.SelectedIndexChanged += (_, _) => UpdateActionButtons();
        panel.Controls.Add(_gameList, 0, 1);

        _launchSelectedButton.Text = "Launch on Selected PC";
        _launchSelectedButton.Dock = DockStyle.Top;
        _launchSelectedButton.Height = 32;
        _launchSelectedButton.Enabled = false;
        _launchSelectedButton.Click += (_, _) => LaunchSelectedPc();
        panel.Controls.Add(_launchSelectedButton, 0, 2);

        var button = new Button { Text = "Launch on all", Dock = DockStyle.Top, Height = 32 };
        button.Click += (_, _) => LaunchSelected();
        panel.Controls.Add(button, 0, 3);

        _testButton.Text = "Test: Open Notepad";
        _testButton.Dock = DockStyle.Top;
        _testButton.Height = 32;
        _testButton.Enabled = false;
        _testButton.Click += (_, _) => SendTestCommand();
        panel.Controls.Add(_testButton, 0, 4);

        _testMissingButton.Text = "Test: Missing helpme.exe";
        _testMissingButton.Dock = DockStyle.Top;
        _testMissingButton.Height = 32;
        _testMissingButton.Enabled = false;
        _testMissingButton.Click += (_, _) => SendMissingExeTest();
        panel.Controls.Add(_testMissingButton, 0, 5);

        _shutdownButton.Text = "Close Remote DadBoard";
        _shutdownButton.Dock = DockStyle.Top;
        _shutdownButton.Height = 32;
        _shutdownButton.Enabled = false;
        _shutdownButton.Click += (_, _) => SendShutdownCommand();
        panel.Controls.Add(_shutdownButton, 0, 6);

        return panel;
    }

    private Control BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.SelectionChanged += (_, _) => UpdateActionButtons();
        _grid.MouseDown += GridMouseDown;
        _grid.ContextMenuStrip = _rowMenu;

        _rowMenu.Items.AddRange(new ToolStripItem[]
        {
            _menuLaunch,
            _menuTest,
            new ToolStripSeparator(),
            _menuCopyPcId,
            _menuCopyIp,
            _menuViewError
        });
        _rowMenu.Opening += (_, e) =>
        {
            if (_grid.SelectedRows.Count != 1)
            {
                e.Cancel = true;
                return;
            }

            UpdateContextMenuState();
        };
        _menuLaunch.Click += (_, _) => LaunchSelectedPc();
        _menuTest.Click += (_, _) => SendTestCommand();
        _menuCopyPcId.Click += (_, _) => CopySelectedValue("pcId");
        _menuCopyIp.Click += (_, _) => CopySelectedValue("ip");
        _menuViewError.Click += (_, _) => ShowSelectedError();

        EnsureGridColumns();

        return _grid;
    }

    private void RefreshGridSafe()
    {
        if (IsDisposed || Disposing || !_grid.IsHandleCreated || _grid.IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(RefreshGridSafe));
            return;
        }

        if (System.Threading.Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
        if (_grid.Columns.Count == 0)
        {
            EnsureGridColumns();
        }

        var selectedPcId = GetSelectedAgentPcId();
        var snapshot = _service.GetAgentsSnapshot();
        _grid.Rows.Clear();

        foreach (var agent in snapshot)
        {
            var onlineText = FormatOnline(agent.Online, agent.LastSeen);
            var commandStatus = FormatCommandStatus(agent.LastStatus);
            var ackText = FormatAck(agent.LastAckTs, agent.LastAckOk, agent.LastAckError);
            var resultText = string.IsNullOrWhiteSpace(agent.LastResult) ? "-" : agent.LastResult;
            var lastError = agent.LastError ?? "";
            var truncatedError = Truncate(lastError, 60);

            _grid.Rows.Add(
                agent.PcId,
                agent.Ip,
                agent.Name,
                onlineText,
                commandStatus,
                ackText,
                resultText,
                truncatedError
            );

            var row = _grid.Rows[^1];
            row.Cells["error"].ToolTipText = lastError;
        }

        _grid.ClearSelection();
        if (!string.IsNullOrWhiteSpace(selectedPcId))
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Cells["pcId"].Value?.ToString() == selectedPcId)
                {
                    row.Selected = true;
                    break;
                }
            }
        }

        UpdateActionButtons();
        var onlineSummary = $"Agents online: {snapshot.Count(a => a.Online)} / {snapshot.Count}";
        var selectedFailure = GetSelectedFailureMessage();
        _statusLabel.Text = string.IsNullOrWhiteSpace(selectedFailure) ? onlineSummary : selectedFailure;
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void EnsureGridColumns()
    {
        if (_grid.Columns.Count > 0)
        {
            return;
        }

        var pcIdCol = _grid.Columns.Add("pcId", "PC Id");
        _grid.Columns[pcIdCol].Visible = false;
        var ipCol = _grid.Columns.Add("ip", "IP");
        _grid.Columns[ipCol].Visible = false;
        _grid.Columns.Add("name", "PC Name");
        _grid.Columns.Add("online", "Online");
        _grid.Columns.Add("command", "Command Status");
        _grid.Columns.Add("ack", "Ack");
        _grid.Columns.Add("result", "Last Result");
        _grid.Columns.Add("error", "Last Error");
    }

    private void LaunchSelected()
    {
        if (_gameList.SelectedItem is not GameDefinition game)
        {
            MessageBox.Show("Select a game first.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _service.LaunchOnAll(game);
        _statusLabel.Text = $"Launch triggered: {game.Name}";
    }

    private void LaunchSelectedPc()
    {
        if (_gameList.SelectedItem is not GameDefinition game)
        {
            MessageBox.Show("Select a game first.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var pcId = GetSelectedAgentPcId();
        if (string.IsNullOrWhiteSpace(pcId))
        {
            MessageBox.Show("Select an agent row first.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!_service.LaunchOnAgent(game, pcId, out var error))
        {
            MessageBox.Show(error ?? "Unable to launch on selected PC.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _statusLabel.Text = $"Launch triggered: {game.Name} (selected PC)";
    }

    private void SendTestCommand()
    {
        var pcId = GetSelectedAgentPcId();
        var ip = GetSelectedAgentIp();
        if (string.IsNullOrWhiteSpace(pcId) || string.IsNullOrWhiteSpace(ip))
        {
            MessageBox.Show("Select an agent row first.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_service.IsLocalAgent(pcId, ip))
        {
            MessageBox.Show("Select a remote agent (not this PC).", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!_service.SendTestCommand(pcId, out var error))
        {
            MessageBox.Show(error ?? "Unable to send test command.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _statusLabel.Text = "Sent test command: notepad.exe";
    }

    private string? GetSelectedAgentPcId()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            return null;
        }

        var row = _grid.SelectedRows[0];
        return row.Cells["pcId"].Value?.ToString();
    }

    private string? GetSelectedAgentIp()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            return null;
        }

        var row = _grid.SelectedRows[0];
        return row.Cells["ip"].Value?.ToString();
    }

    private void UpdateActionButtons()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            _launchSelectedButton.Enabled = false;
            _testButton.Enabled = false;
            _testMissingButton.Enabled = false;
            _shutdownButton.Enabled = false;
            return;
        }

        var pcId = GetSelectedAgentPcId();
        var ip = GetSelectedAgentIp();
        if (string.IsNullOrWhiteSpace(pcId) || string.IsNullOrWhiteSpace(ip))
        {
            _launchSelectedButton.Enabled = false;
            _testButton.Enabled = false;
            _testMissingButton.Enabled = false;
            _shutdownButton.Enabled = false;
            return;
        }

        var enabled = !_service.IsLocalAgent(pcId, ip);
        _launchSelectedButton.Enabled = _grid.SelectedRows.Count == 1;
        _testButton.Enabled = enabled;
        _testMissingButton.Enabled = enabled;
        _shutdownButton.Enabled = enabled;
    }

    private void SendMissingExeTest()
    {
        var pcId = GetSelectedAgentPcId();
        var ip = GetSelectedAgentIp();
        if (string.IsNullOrWhiteSpace(pcId) || string.IsNullOrWhiteSpace(ip))
        {
            MessageBox.Show("Select an agent row first.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_service.IsLocalAgent(pcId, ip))
        {
            MessageBox.Show("Select a remote agent (not this PC).", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!_service.SendTestCommand(pcId, "helpme.exe", out var error))
        {
            MessageBox.Show(error ?? "Unable to send test command.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _statusLabel.Text = "Sent test command: helpme.exe";
    }

    private string? GetSelectedFailureMessage()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            return null;
        }

        var row = _grid.SelectedRows[0];
        var status = row.Cells["command"].Value?.ToString();
        var message = row.Cells["error"].ToolTipText;
        if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(message))
        {
            return $"Selected failed: {message}";
        }

        return null;
    }

    private void SendShutdownCommand()
    {
        var pcId = GetSelectedAgentPcId();
        var ip = GetSelectedAgentIp();
        if (string.IsNullOrWhiteSpace(pcId) || string.IsNullOrWhiteSpace(ip))
        {
            MessageBox.Show("Select an agent row first.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_service.IsLocalAgent(pcId, ip))
        {
            MessageBox.Show("Select a remote agent (not this PC).", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!_service.SendShutdownCommand(pcId, out var error))
        {
            MessageBox.Show(error ?? "Unable to send shutdown command.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _statusLabel.Text = "Sent shutdown command.";
    }

    private void GridMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        var hit = _grid.HitTest(e.X, e.Y);
        if (hit.RowIndex < 0)
        {
            return;
        }

        _grid.ClearSelection();
        _grid.Rows[hit.RowIndex].Selected = true;
        UpdateActionButtons();
    }

    private void UpdateContextMenuState()
    {
        var pcId = GetSelectedAgentPcId();
        var ip = GetSelectedAgentIp();
        var hasSelection = !string.IsNullOrWhiteSpace(pcId) && !string.IsNullOrWhiteSpace(ip);
        var isLocal = hasSelection && _service.IsLocalAgent(pcId!, ip!);
        _menuLaunch.Enabled = hasSelection;
        _menuTest.Enabled = hasSelection && !isLocal;
        _menuCopyPcId.Enabled = hasSelection;
        _menuCopyIp.Enabled = hasSelection;
        _menuViewError.Enabled = hasSelection;
    }

    private void CopySelectedValue(string column)
    {
        if (_grid.SelectedRows.Count == 0)
        {
            return;
        }

        var value = _grid.SelectedRows[0].Cells[column].Value?.ToString();
        if (!string.IsNullOrWhiteSpace(value))
        {
            Clipboard.SetText(value);
        }
    }

    private void ShowSelectedError()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            return;
        }

        var message = _grid.SelectedRows[0].Cells["error"].ToolTipText;
        if (string.IsNullOrWhiteSpace(message))
        {
            MessageBox.Show("No error recorded for this PC.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        MessageBox.Show(message, "DadBoard - Last Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static string FormatOnline(bool online, DateTime lastSeen)
    {
        var last = lastSeen == default ? "-" : lastSeen.ToLocalTime().ToString("HH:mm:ss");
        return online ? $"Online ({last})" : $"Offline ({last})";
    }

    private static string FormatCommandStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return "Idle";
        }

        return status.ToLowerInvariant() switch
        {
            "running" => "Running",
            "failed" => "Failed",
            "timeout" => "Timed out",
            "ws_error" => "Failed",
            "sent" => "Running",
            "received" => "Running",
            "launching" => "Running",
            "stopping" => "Running",
            _ => "Running"
        };
    }

    private static string FormatAck(DateTime lastAckTs, bool ok, string error)
    {
        if (lastAckTs == default)
        {
            return "-";
        }

        var time = lastAckTs.ToLocalTime().ToString("HH:mm:ss");
        if (!ok)
        {
            return $"ERR {time}";
        }

        return $"OK {time}";
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
        {
            return text;
        }

        return text[..max] + "...";
    }

    private void StartRefresh()
    {
        if (_refreshTimer.Enabled)
        {
            return;
        }

        RefreshGridSafe();
        _refreshTimer.Start();
    }

    private void StopRefresh()
    {
        if (!_refreshTimer.Enabled)
        {
            return;
        }

        _refreshTimer.Stop();
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= _refreshHandler;
        _refreshTimer.Dispose();
    }
}
