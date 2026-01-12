using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DadBoard.Leader;

namespace DadBoard.App;

sealed class StatusForm : Form
{
    private readonly Label _header = new();
    private readonly DataGridView _agentsGrid = new();
    private readonly DataGridView _connectionsGrid = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly UpdateLogger _logger = new();
    private LeaderService? _leader;
    private bool _timerStopped;
    private bool _allowClose;
    private int _refreshing;

    public StatusForm()
    {
        Text = "DadBoard Status";
        Size = new Size(900, 520);
        StartPosition = FormStartPosition.CenterScreen;
        FormClosing += OnFormClosing;
        FormClosed += (_, _) => StopRefreshTimer();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));

        _header.AutoSize = true;
        layout.Controls.Add(_header, 0, 0);

        ConfigureGrid(_agentsGrid, "Agents");
        ConfigureGrid(_connectionsGrid, "Connections");

        layout.Controls.Add(_agentsGrid, 0, 1);
        layout.Controls.Add(_connectionsGrid, 0, 2);

        Controls.Add(layout);

        _refreshTimer.Interval = 1000;
        _refreshTimer.Tick += (_, _) => RefreshData();
        _refreshTimer.Start();

        VisibleChanged += (_, _) =>
        {
            if (Visible)
            {
                StartRefreshTimer();
            }
            else
            {
                StopRefreshTimer();
            }
        };
    }

    public void UpdateLeader(LeaderService? leader)
    {
        _leader = leader;
        RefreshData();
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    private void ConfigureGrid(DataGridView grid, string name)
    {
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.RowHeadersVisible = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.Columns.Clear();

        if (name == "Agents")
        {
            grid.Columns.Add("name", "PC");
            grid.Columns.Add("ip", "IP");
            grid.Columns.Add("online", "Online");
            grid.Columns.Add("lastSeen", "Last Seen");
            grid.Columns.Add("status", "Status");
            grid.Columns.Add("message", "Message");
        }
        else
        {
            grid.Columns.Add("pc", "PC");
            grid.Columns.Add("endpoint", "Endpoint");
            grid.Columns.Add("state", "State");
            grid.Columns.Add("last", "Last Message");
            grid.Columns.Add("error", "Last Error");
        }
    }

    private void RefreshData()
    {
        if (System.Threading.Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        if (IsDisposed || Disposing || _agentsGrid.IsDisposed || _connectionsGrid.IsDisposed)
        {
            System.Threading.Interlocked.Exchange(ref _refreshing, 0);
            return;
        }

        if (_agentsGrid.Columns.Count == 0 || _connectionsGrid.Columns.Count == 0)
        {
            System.Threading.Interlocked.Exchange(ref _refreshing, 0);
            return;
        }

        try
        {
            if (_leader == null)
            {
                _header.Text = "Leader is disabled. Enable Leader to see discovery and connections.";
                _agentsGrid.Rows.Clear();
                _connectionsGrid.Rows.Clear();
                return;
            }

            var agents = _leader.GetAgentsSnapshot();
            var connections = _leader.GetConnectionsSnapshot();

            _header.Text = $"Leader enabled. Agents online: {agents.Count(a => a.Online)} / {agents.Count}";

            _agentsGrid.Rows.Clear();
            foreach (var agent in agents)
            {
                _agentsGrid.Rows.Add(
                    agent.Name,
                    agent.Ip,
                    agent.Online ? "Yes" : "No",
                    agent.LastSeen == default ? "-" : agent.LastSeen.ToLocalTime().ToString("HH:mm:ss"),
                    agent.LastStatus,
                    agent.LastStatusMessage
                );
            }

            _connectionsGrid.Rows.Clear();
            foreach (var conn in connections)
            {
                var lastMessage = "-";
                if (!string.IsNullOrWhiteSpace(conn.LastMessage) && DateTime.TryParse(conn.LastMessage, out var dt))
                {
                    lastMessage = dt.ToLocalTime().ToString("HH:mm:ss");
                }

                _connectionsGrid.Rows.Add(
                    conn.PcId,
                    conn.Endpoint,
                    conn.State,
                    lastMessage,
                    conn.LastError
                );
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"StatusForm refresh failed: {ex}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            StopRefreshTimer();
            return;
        }

        StopRefreshTimer();
    }

    private void StartRefreshTimer()
    {
        if (_timerStopped)
        {
            return;
        }

        if (!_refreshTimer.Enabled)
        {
            _refreshTimer.Start();
        }
    }

    private void StopRefreshTimer()
    {
        if (_timerStopped)
        {
            return;
        }

        _timerStopped = true;
        try
        {
            if (_refreshTimer.Enabled)
            {
                _refreshTimer.Stop();
            }
        }
        catch
        {
        }

        _refreshTimer.Dispose();
    }
}
