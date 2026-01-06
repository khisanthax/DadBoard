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
        _refreshTimer.Start();

        Load += (_, _) => RefreshGridSafe();
        FormClosing += OnFormClosing;
        FormClosed += OnFormClosed;
    }

    private Control BuildLeftPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        panel.Controls.Add(new Label { Text = "Games", AutoSize = true }, 0, 0);

        _gameList.Dock = DockStyle.Fill;
        _gameList.DisplayMember = "Name";
        foreach (var game in _service.GetGames())
        {
            _gameList.Items.Add(game);
        }
        panel.Controls.Add(_gameList, 0, 1);

        var button = new Button { Text = "Launch on all", Dock = DockStyle.Top, Height = 32 };
        button.Click += (_, _) => LaunchSelected();
        panel.Controls.Add(button, 0, 2);

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

        if (_grid.Columns.Count == 0)
        {
            EnsureGridColumns();
        }

        var snapshot = _service.GetAgentsSnapshot();
        _grid.Rows.Clear();

        foreach (var agent in snapshot)
        {
            _grid.Rows.Add(
                agent.Name,
                agent.Ip,
                agent.Online ? "Yes" : "No",
                agent.LastSeen == default ? "-" : agent.LastSeen.ToLocalTime().ToString("HH:mm:ss"),
                agent.LastStatus,
                agent.LastStatusMessage
            );
        }

        _statusLabel.Text = $"Agents online: {snapshot.Count(a => a.Online)} / {snapshot.Count}";
    }

    private void EnsureGridColumns()
    {
        if (_grid.Columns.Count > 0)
        {
            return;
        }

        _grid.Columns.Add("name", "PC");
        _grid.Columns.Add("ip", "IP");
        _grid.Columns.Add("online", "Online");
        _grid.Columns.Add("lastSeen", "Last Seen");
        _grid.Columns.Add("status", "Status");
        _grid.Columns.Add("message", "Message");
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
