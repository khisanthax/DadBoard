
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DadBoard.Spine.Shared;

namespace DadBoard.Leader;

public sealed class LeaderForm : Form
{
    private readonly LeaderService _service;
    private readonly TabControl _tabs = new();
    private readonly TabPage _agentsTab = new("Agents");
    private readonly TabPage _gamesTab = new("Games");

    private readonly DataGridView _agentsGrid = new();
    private readonly DataGridView _gamesGrid = new();
    private readonly Label _statusLabel = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly EventHandler _refreshHandler;

    private readonly Button _refreshGamesButton = new();
    private readonly CheckBox _showAllGamesToggle = new();
    private readonly Button _launchSelectedButton = new();
    private readonly Button _launchCheckedButton = new();
    private readonly Button _launchAllAvailableButton = new();
    private readonly Button _selectAllAvailableButton = new();

    private readonly Button _launchOnSelectedAgentButton = new();
    private readonly Button _testButton = new();
    private readonly Button _testMissingButton = new();
    private readonly Button _shutdownButton = new();

    private readonly ContextMenuStrip _rowMenu = new();
    private readonly ToolStripMenuItem _menuLaunch = new("Launch on this PC");
    private readonly ToolStripMenuItem _menuTest = new("Test: Open Notepad");
    private readonly ToolStripMenuItem _menuCopyPcId = new("Copy PC ID");
    private readonly ToolStripMenuItem _menuCopyIp = new("Copy IP");
    private readonly ToolStripMenuItem _menuViewError = new("View last error");

    private readonly Dictionary<string, string> _gamePcColumnMap = new(StringComparer.OrdinalIgnoreCase);
    private int _refreshing;
    private bool _gamesDirty = true;
    private bool _allowClose;

    public LeaderForm(LeaderService service)
    {
        _service = service;
        Text = "DadBoard Leader (Phase 3)";
        Size = new Size(1120, 640);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _tabs.Dock = DockStyle.Fill;
        _agentsTab.Controls.Add(BuildAgentsTab());
        _gamesTab.Controls.Add(BuildGamesTab());
        _tabs.TabPages.Add(_agentsTab);
        _tabs.TabPages.Add(_gamesTab);
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedTab == _gamesTab)
            {
                _gamesDirty = true;
                RefreshGamesGridSafe();
            }
        };

        layout.Controls.Add(_tabs, 0, 0);
        layout.Controls.Add(_statusLabel, 0, 1);
        Controls.Add(layout);

        _refreshTimer.Interval = 1000;
        _refreshHandler = (_, _) => RefreshAllSafe();
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

        _service.InventoriesUpdated += () =>
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            BeginInvoke(new Action(() =>
            {
                _gamesDirty = true;
                if (_tabs.SelectedTab == _gamesTab)
                {
                    RefreshGamesGridSafe();
                }
            }));
        };

        FormClosing += OnFormClosing;
        FormClosed += OnFormClosed;
    }

    private Control BuildAgentsTab()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true
        };

        _launchOnSelectedAgentButton.Text = "Launch selected game";
        _launchOnSelectedAgentButton.Height = 32;
        _launchOnSelectedAgentButton.Enabled = false;
        _launchOnSelectedAgentButton.Click += (_, _) => LaunchSelectedGameOnAgentFromMenu();

        _testButton.Text = "Test: Open Notepad";
        _testButton.Height = 32;
        _testButton.Enabled = false;
        _testButton.Click += (_, _) => SendTestCommand();

        _testMissingButton.Text = "Test: Missing helpme.exe";
        _testMissingButton.Height = 32;
        _testMissingButton.Enabled = false;
        _testMissingButton.Click += (_, _) => SendMissingExeTest();

        _shutdownButton.Text = "Close Remote DadBoard";
        _shutdownButton.Height = 32;
        _shutdownButton.Enabled = false;
        _shutdownButton.Click += (_, _) => SendShutdownCommand();

        actions.Controls.AddRange(new Control[]
        {
            _launchOnSelectedAgentButton,
            _testButton,
            _testMissingButton,
            _shutdownButton
        });

        panel.Controls.Add(actions, 0, 0);
        panel.Controls.Add(BuildAgentsGrid(), 0, 1);
        return panel;
    }

    private Control BuildGamesTab()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true
        };

        _refreshGamesButton.Text = "Refresh games";
        _refreshGamesButton.Height = 32;
        _refreshGamesButton.Click += (_, _) => RefreshGames();

        _showAllGamesToggle.Text = "Show all games (including leader-only)";
        _showAllGamesToggle.AutoSize = true;
        _showAllGamesToggle.CheckedChanged += (_, _) =>
        {
            _gamesDirty = true;
            RefreshGamesGridSafe();
        };

        _launchSelectedButton.Text = "Launch on Selected PC";
        _launchSelectedButton.Height = 32;
        _launchSelectedButton.Enabled = false;
        _launchSelectedButton.Click += (_, _) => LaunchSelectedGameOnSelectedPc();

        _launchCheckedButton.Text = "Launch on Checked PCs";
        _launchCheckedButton.Height = 32;
        _launchCheckedButton.Enabled = false;
        _launchCheckedButton.Click += (_, _) => LaunchSelectedGameOnCheckedPcs();

        _launchAllAvailableButton.Text = "Launch on All Available";
        _launchAllAvailableButton.Height = 32;
        _launchAllAvailableButton.Enabled = false;
        _launchAllAvailableButton.Click += (_, _) => LaunchSelectedGameOnAllAvailable();

        _selectAllAvailableButton.Text = "Select all available PCs";
        _selectAllAvailableButton.Height = 32;
        _selectAllAvailableButton.Enabled = false;
        _selectAllAvailableButton.Click += (_, _) => SelectAllAvailableForSelectedGame();

        actions.Controls.AddRange(new Control[]
        {
            _refreshGamesButton,
            _showAllGamesToggle,
            _launchSelectedButton,
            _launchCheckedButton,
            _launchAllAvailableButton,
            _selectAllAvailableButton
        });

        panel.Controls.Add(actions, 0, 0);
        panel.Controls.Add(BuildGamesGrid(), 0, 1);
        return panel;
    }
    private Control BuildAgentsGrid()
    {
        _agentsGrid.Dock = DockStyle.Fill;
        _agentsGrid.ReadOnly = true;
        _agentsGrid.AllowUserToAddRows = false;
        _agentsGrid.AllowUserToDeleteRows = false;
        _agentsGrid.RowHeadersVisible = false;
        _agentsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _agentsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _agentsGrid.MultiSelect = false;
        _agentsGrid.SelectionChanged += (_, _) => UpdateAgentActions();
        _agentsGrid.MouseDown += AgentsGridMouseDown;
        _agentsGrid.ContextMenuStrip = _rowMenu;

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
            if (_agentsGrid.SelectedRows.Count != 1)
            {
                e.Cancel = true;
                return;
            }

            UpdateContextMenuState();
        };
        _menuLaunch.Click += (_, _) => LaunchSelectedGameOnAgentFromMenu();
        _menuTest.Click += (_, _) => SendTestCommand();
        _menuCopyPcId.Click += (_, _) => CopySelectedAgentValue("pcId");
        _menuCopyIp.Click += (_, _) => CopySelectedAgentValue("ip");
        _menuViewError.Click += (_, _) => ShowSelectedAgentError();

        EnsureAgentsGridColumns();
        return _agentsGrid;
    }

    private Control BuildGamesGrid()
    {
        _gamesGrid.Dock = DockStyle.Fill;
        _gamesGrid.ReadOnly = false;
        _gamesGrid.AllowUserToAddRows = false;
        _gamesGrid.AllowUserToDeleteRows = false;
        _gamesGrid.RowHeadersVisible = false;
        _gamesGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gamesGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _gamesGrid.MultiSelect = false;
        _gamesGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_gamesGrid.IsCurrentCellDirty)
            {
                _gamesGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _gamesGrid.CellValueChanged += (_, _) => UpdateGameActions();
        _gamesGrid.SelectionChanged += (_, _) => UpdateGameActions();

        return _gamesGrid;
    }

    private void RefreshAllSafe()
    {
        RefreshAgentsGridSafe();
        if (_tabs.SelectedTab == _gamesTab && _gamesDirty)
        {
            RefreshGamesGridSafe();
        }
    }

    private void RefreshAgentsGridSafe()
    {
        if (IsDisposed || Disposing || !_agentsGrid.IsHandleCreated || _agentsGrid.IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(RefreshAgentsGridSafe));
            return;
        }

        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            if (_agentsGrid.Columns.Count == 0)
            {
                EnsureAgentsGridColumns();
            }

            var selectedPcId = GetSelectedAgentPcId();
            var snapshot = _service.GetAgentsSnapshot();
            _agentsGrid.Rows.Clear();

            foreach (var agent in snapshot)
            {
                var onlineText = FormatOnline(agent.Online, agent.LastSeen);
                var commandStatus = FormatCommandStatus(agent.LastStatus);
                var ackText = FormatAck(agent.LastAckTs, agent.LastAckOk, agent.LastAckError);
                var resultText = string.IsNullOrWhiteSpace(agent.LastResult) ? "-" : agent.LastResult;
                var lastError = agent.LastError ?? "";
                var truncatedError = Truncate(lastError, 60);

                _agentsGrid.Rows.Add(
                    agent.PcId,
                    agent.Ip,
                    agent.Name,
                    onlineText,
                    commandStatus,
                    ackText,
                    resultText,
                    truncatedError
                );

                var row = _agentsGrid.Rows[^1];
                row.Cells["error"].ToolTipText = lastError;
            }

            _agentsGrid.ClearSelection();
            if (!string.IsNullOrWhiteSpace(selectedPcId))
            {
                foreach (DataGridViewRow row in _agentsGrid.Rows)
                {
                    if (row.Cells["pcId"].Value?.ToString() == selectedPcId)
                    {
                        row.Selected = true;
                        break;
                    }
                }
            }

            UpdateAgentActions();
            var onlineSummary = $"Agents online: {snapshot.Count(a => a.Online)} / {snapshot.Count}";
            var selectedFailure = GetSelectedAgentFailureMessage();
            _statusLabel.Text = string.IsNullOrWhiteSpace(selectedFailure) ? onlineSummary : selectedFailure;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }
    private void RefreshGamesGridSafe()
    {
        if (IsDisposed || Disposing || !_gamesGrid.IsHandleCreated || _gamesGrid.IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(RefreshGamesGridSafe));
            return;
        }

        var selections = CaptureGameSelections();
        var selectedAppId = GetSelectedGameAppId();

        var leaderCatalog = _service.GetLeaderCatalog();
        var agentInventories = _service.GetAgentInventoriesSnapshot();
        var remoteAgents = _service.GetAgentsSnapshot()
            .Where(a => !_service.IsLocalAgent(a.PcId, a.Ip))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        BuildGamesGridColumns(remoteAgents);

        var availableAppIds = new HashSet<int>();
        foreach (var inventory in agentInventories.Values)
        {
            foreach (var game in inventory.Games)
            {
                availableAppIds.Add(game.AppId);
            }
        }

        var filtered = leaderCatalog
            .Where(g => g.AppId > 0)
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!_showAllGamesToggle.Checked)
        {
            filtered = filtered.Where(g => availableAppIds.Contains(g.AppId)).ToList();
        }

        _gamesGrid.Rows.Clear();
        foreach (var game in filtered)
        {
            var rowIndex = _gamesGrid.Rows.Add(game.AppId, game.Name);
            var row = _gamesGrid.Rows[rowIndex];

            foreach (var agent in remoteAgents)
            {
                if (!_gamePcColumnMap.TryGetValue(agent.PcId, out var columnName))
                {
                    continue;
                }

                var hasInventory = agentInventories.TryGetValue(agent.PcId, out var inventory);
                var installed = hasInventory && inventory!.Games.Any(g => g.AppId == game.AppId);

                var cell = new DataGridViewCheckBoxCell
                {
                    Value = selections.TryGetValue(game.AppId, out var pcSet) && pcSet.Contains(agent.PcId)
                };

                if (!installed)
                {
                    cell.ReadOnly = true;
                    cell.Style.ForeColor = Color.Gray;
                    cell.ToolTipText = "Not installed on this PC";
                }
                else
                {
                    cell.ToolTipText = "Installed. Check to include.";
                }

                row.Cells[columnName] = cell;
            }
        }

        _gamesGrid.ClearSelection();
        if (selectedAppId.HasValue)
        {
            foreach (DataGridViewRow row in _gamesGrid.Rows)
            {
                if (row.Cells["appId"].Value is int appId && appId == selectedAppId.Value)
                {
                    row.Selected = true;
                    break;
                }
            }
        }

        _gamesDirty = false;
        UpdateGameActions();
    }

    private void EnsureAgentsGridColumns()
    {
        if (_agentsGrid.Columns.Count > 0)
        {
            return;
        }

        var pcIdCol = _agentsGrid.Columns.Add("pcId", "PC Id");
        _agentsGrid.Columns[pcIdCol].Visible = false;
        var ipCol = _agentsGrid.Columns.Add("ip", "IP");
        _agentsGrid.Columns[ipCol].Visible = false;
        _agentsGrid.Columns.Add("name", "PC Name");
        _agentsGrid.Columns.Add("online", "Online");
        _agentsGrid.Columns.Add("command", "Command Status");
        _agentsGrid.Columns.Add("ack", "Ack");
        _agentsGrid.Columns.Add("result", "Last Result");
        _agentsGrid.Columns.Add("error", "Last Error");
    }

    private void BuildGamesGridColumns(IEnumerable<AgentInfo> remoteAgents)
    {
        _gamesGrid.Columns.Clear();
        _gamePcColumnMap.Clear();

        var appIdCol = _gamesGrid.Columns.Add("appId", "App Id");
        _gamesGrid.Columns[appIdCol].Visible = false;
        _gamesGrid.Columns.Add("name", "Game");

        foreach (var agent in remoteAgents)
        {
            var columnName = $"pc_{agent.PcId}";
            var column = new DataGridViewCheckBoxColumn
            {
                Name = columnName,
                HeaderText = agent.Name,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
                TrueValue = true,
                FalseValue = false
            };
            _gamesGrid.Columns.Add(column);
            _gamePcColumnMap[agent.PcId] = columnName;
        }
    }

    private void RefreshGames()
    {
        _gamesDirty = true;
        Task.Run(() => _service.RefreshSteamInventory());
    }

    private void LaunchSelectedGameOnSelectedPc()
    {
        var selection = GetSelectedGame();
        if (selection == null)
        {
            MessageBox.Show("Select a game row first.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var checkedPcs = GetCheckedPcIds(selection.Value.AppId);
        if (checkedPcs.Count != 1)
        {
            MessageBox.Show("Check exactly one PC for this game.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var pcId = checkedPcs[0];
        if (!_service.LaunchAppIdOnAgent(selection.Value.AppId, pcId, out var error))
        {
            MessageBox.Show(error ?? "Unable to launch on selected PC.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _statusLabel.Text = $"Launch triggered: {selection.Value.Name} (selected PC)";
    }

    private void LaunchSelectedGameOnCheckedPcs()
    {
        var selection = GetSelectedGame();
        if (selection == null)
        {
            MessageBox.Show("Select a game row first.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var checkedPcs = GetCheckedPcIds(selection.Value.AppId);
        if (checkedPcs.Count == 0)
        {
            MessageBox.Show("Check at least one PC for this game.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _service.LaunchAppIdOnAgents(selection.Value.AppId, checkedPcs);
        _statusLabel.Text = $"Launch triggered: {selection.Value.Name} ({checkedPcs.Count} PCs)";
    }

    private void LaunchSelectedGameOnAllAvailable()
    {
        var selection = GetSelectedGame();
        if (selection == null)
        {
            MessageBox.Show("Select a game row first.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var available = GetAvailablePcIds(selection.Value.AppId);
        if (available.Count == 0)
        {
            MessageBox.Show("No available PCs for this game.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _service.LaunchAppIdOnAgents(selection.Value.AppId, available);
        _statusLabel.Text = $"Launch triggered: {selection.Value.Name} ({available.Count} PCs)";
    }

    private void SelectAllAvailableForSelectedGame()
    {
        var selection = GetSelectedGame();
        if (selection == null)
        {
            MessageBox.Show("Select a game row first.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_gamesGrid.SelectedRows.Count == 0)
        {
            return;
        }

        var row = _gamesGrid.SelectedRows[0];
        foreach (var entry in _gamePcColumnMap)
        {
            var cell = row.Cells[entry.Value] as DataGridViewCheckBoxCell;
            if (cell == null || cell.ReadOnly)
            {
                continue;
            }

            cell.Value = true;
        }

        UpdateGameActions();
    }

    private void LaunchSelectedGameOnAgentFromMenu()
    {
        var selection = GetSelectedGame();
        if (selection == null)
        {
            MessageBox.Show("Select a game in the Games tab first.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var pcId = GetSelectedAgentPcId();
        if (string.IsNullOrWhiteSpace(pcId))
        {
            MessageBox.Show("Select an agent row first.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!_service.LaunchAppIdOnAgent(selection.Value.AppId, pcId, out var error))
        {
            MessageBox.Show(error ?? "Unable to launch on selected PC.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _statusLabel.Text = $"Launch triggered: {selection.Value.Name} (selected PC)";
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

    private string? GetSelectedAgentPcId()
    {
        if (_agentsGrid.SelectedRows.Count == 0)
        {
            return null;
        }

        var row = _agentsGrid.SelectedRows[0];
        return row.Cells["pcId"].Value?.ToString();
    }

    private string? GetSelectedAgentIp()
    {
        if (_agentsGrid.SelectedRows.Count == 0)
        {
            return null;
        }

        var row = _agentsGrid.SelectedRows[0];
        return row.Cells["ip"].Value?.ToString();
    }

    private (int AppId, string Name)? GetSelectedGame()
    {
        if (_gamesGrid.SelectedRows.Count == 0)
        {
            return null;
        }

        var row = _gamesGrid.SelectedRows[0];
        if (row.Cells["appId"].Value is not int appId)
        {
            return null;
        }

        var name = row.Cells["name"].Value?.ToString() ?? appId.ToString();
        return (appId, name);
    }

    private int? GetSelectedGameAppId()
    {
        var selected = GetSelectedGame();
        return selected?.AppId;
    }

    private Dictionary<int, HashSet<string>> CaptureGameSelections()
    {
        var selections = new Dictionary<int, HashSet<string>>();
        foreach (DataGridViewRow row in _gamesGrid.Rows)
        {
            if (row.Cells["appId"].Value is not int appId)
            {
                continue;
            }

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _gamePcColumnMap)
            {
                if (row.Cells[entry.Value] is DataGridViewCheckBoxCell cell &&
                    cell.Value is bool selected &&
                    selected)
                {
                    set.Add(entry.Key);
                }
            }

            selections[appId] = set;
        }

        return selections;
    }

    private List<string> GetCheckedPcIds(int appId)
    {
        var checkedPcIds = new List<string>();
        foreach (DataGridViewRow row in _gamesGrid.Rows)
        {
            if (row.Cells["appId"].Value is not int rowAppId || rowAppId != appId)
            {
                continue;
            }

            foreach (var entry in _gamePcColumnMap)
            {
                if (row.Cells[entry.Value] is DataGridViewCheckBoxCell cell &&
                    cell.Value is bool selected &&
                    selected)
                {
                    checkedPcIds.Add(entry.Key);
                }
            }
            break;
        }

        return checkedPcIds;
    }

    private List<string> GetAvailablePcIds(int appId)
    {
        var available = new List<string>();
        foreach (DataGridViewRow row in _gamesGrid.Rows)
        {
            if (row.Cells["appId"].Value is not int rowAppId || rowAppId != appId)
            {
                continue;
            }

            foreach (var entry in _gamePcColumnMap)
            {
                if (row.Cells[entry.Value] is DataGridViewCheckBoxCell cell &&
                    !cell.ReadOnly)
                {
                    available.Add(entry.Key);
                }
            }
            break;
        }

        return available;
    }

    private void UpdateAgentActions()
    {
        if (_agentsGrid.SelectedRows.Count == 0)
        {
            _launchOnSelectedAgentButton.Enabled = false;
            _testButton.Enabled = false;
            _testMissingButton.Enabled = false;
            _shutdownButton.Enabled = false;
            return;
        }

        var pcId = GetSelectedAgentPcId();
        var ip = GetSelectedAgentIp();
        if (string.IsNullOrWhiteSpace(pcId) || string.IsNullOrWhiteSpace(ip))
        {
            _launchOnSelectedAgentButton.Enabled = false;
            _testButton.Enabled = false;
            _testMissingButton.Enabled = false;
            _shutdownButton.Enabled = false;
            return;
        }

        var enabled = !_service.IsLocalAgent(pcId, ip);
        _launchOnSelectedAgentButton.Enabled = GetSelectedGame() != null;
        _testButton.Enabled = enabled;
        _testMissingButton.Enabled = enabled;
        _shutdownButton.Enabled = enabled;
    }

    private void UpdateGameActions()
    {
        var selection = GetSelectedGame();
        if (selection == null)
        {
            _launchSelectedButton.Enabled = false;
            _launchCheckedButton.Enabled = false;
            _launchAllAvailableButton.Enabled = false;
            _selectAllAvailableButton.Enabled = false;
            UpdateAgentActions();
            return;
        }

        var checkedPcs = GetCheckedPcIds(selection.Value.AppId);
        var available = GetAvailablePcIds(selection.Value.AppId);
        _launchSelectedButton.Enabled = checkedPcs.Count == 1;
        _launchCheckedButton.Enabled = checkedPcs.Count > 0;
        _launchAllAvailableButton.Enabled = available.Count > 0;
        _selectAllAvailableButton.Enabled = available.Count > 0;
        UpdateAgentActions();
    }

    private void AgentsGridMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        var hit = _agentsGrid.HitTest(e.X, e.Y);
        if (hit.RowIndex < 0)
        {
            return;
        }

        _agentsGrid.ClearSelection();
        _agentsGrid.Rows[hit.RowIndex].Selected = true;
        UpdateAgentActions();
    }

    private void UpdateContextMenuState()
    {
        var pcId = GetSelectedAgentPcId();
        var ip = GetSelectedAgentIp();
        var hasSelection = !string.IsNullOrWhiteSpace(pcId) && !string.IsNullOrWhiteSpace(ip);
        var isLocal = hasSelection && _service.IsLocalAgent(pcId!, ip!);
        _menuLaunch.Enabled = hasSelection && GetSelectedGame() != null;
        _menuTest.Enabled = hasSelection && !isLocal;
        _menuCopyPcId.Enabled = hasSelection;
        _menuCopyIp.Enabled = hasSelection;
        _menuViewError.Enabled = hasSelection;
    }

    private void CopySelectedAgentValue(string column)
    {
        if (_agentsGrid.SelectedRows.Count == 0)
        {
            return;
        }

        var value = _agentsGrid.SelectedRows[0].Cells[column].Value?.ToString();
        if (!string.IsNullOrWhiteSpace(value))
        {
            Clipboard.SetText(value);
        }
    }

    private void ShowSelectedAgentError()
    {
        if (_agentsGrid.SelectedRows.Count == 0)
        {
            return;
        }

        var message = _agentsGrid.SelectedRows[0].Cells["error"].ToolTipText;
        if (string.IsNullOrWhiteSpace(message))
        {
            MessageBox.Show("No error recorded for this PC.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        MessageBox.Show(message, "DadBoard - Last Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private string? GetSelectedAgentFailureMessage()
    {
        if (_agentsGrid.SelectedRows.Count == 0)
        {
            return null;
        }

        var row = _agentsGrid.SelectedRows[0];
        var status = row.Cells["command"].Value?.ToString();
        var message = row.Cells["error"].ToolTipText;
        if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(message))
        {
            return $"Selected failed: {message}";
        }

        return null;
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
            _ => "Idle"
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

        RefreshAllSafe();
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
