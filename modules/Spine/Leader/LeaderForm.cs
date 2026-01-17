
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DadBoard.Spine.Shared;

namespace DadBoard.Leader;

public sealed class LeaderForm : Form
{
    private const int GamesListMinWidth = 400;
    private const int LaunchTargetsMinWidth = 280;
    private const int GamesSplitterWidth = 8;
    private const int GamesMinWindowWidth = GamesListMinWidth + LaunchTargetsMinWidth + GamesSplitterWidth + 32;
    private const int GamesMinWindowHeight = 520;

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
    private readonly CheckBox _requireAllToggle = new();
    private readonly Label _filterSummaryLabel = new();
    private readonly FlowLayoutPanel _filterPanel = new();
    private readonly FlowLayoutPanel _targetPanel = new();
    private readonly Button _launchAllOnlineButton = new();
    private readonly Button _launchSelectedButton = new();
    private readonly Button _restartSteamButton = new();
    private readonly Button _selectAllOnlineButton = new();
    private readonly Button _clearTargetsButton = new();
    private readonly SplitContainer _gamesSplit = new();
    private readonly System.Windows.Forms.Timer _splitterDebounceTimer = new();
    private DateTime _lastSplitterWarn = DateTime.MinValue;
    private DateTime _lastSplitterInfo = DateTime.MinValue;
    private int _lastSplitterWarnWidth = -1;
    private int _lastSplitterWarnMin = -1;
    private int _lastSplitterWarnMax = -1;

    private readonly Button _launchOnSelectedAgentButton = new();
    private readonly Button _testButton = new();
    private readonly Button _testMissingButton = new();
    private readonly Button _shutdownButton = new();
    private readonly Button _updateSelectedButton = new();
    private readonly Button _updateAllButton = new();

    private readonly ContextMenuStrip _rowMenu = new();
    private readonly ToolStripMenuItem _menuLaunch = new("Launch on this PC");
    private readonly ToolStripMenuItem _menuTest = new("Test: Open Notepad");
    private readonly ToolStripMenuItem _menuCopyPcId = new("Copy PC ID");
    private readonly ToolStripMenuItem _menuCopyIp = new("Copy IP");
    private readonly ToolStripMenuItem _menuViewError = new("View last error");

    private readonly Dictionary<string, string> _gamePcColumnMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _filterCheckboxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _targetCheckboxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Label> _targetStatusLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FlowLayoutPanel> _targetRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _targetEligibility = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolTip _toolTip = new();
    private readonly HashSet<string> _selectedTargets = new(StringComparer.OrdinalIgnoreCase);
    private int _refreshing;
    private bool _gamesDirty = true;
    private bool _allowClose;
    private HashSet<string>? _pendingLaunchTargets;
    private int? _pendingLaunchAppId;
    private int? _lastSelectedGameAppId;
    private bool _suppressTargetEvents;
    private bool _lastLaunchAllEnabled;
    private bool _lastLaunchSelectedEnabled;
    private string _lastLaunchReason = "";
    private bool _isRefreshingGames;
    private bool _deferredInventoryRefresh;

    public LeaderForm(LeaderService service)
    {
        _service = service;
        Text = "DadBoard Leader (Phase 3)";
        Size = new Size(1120, 640);
        MinimumSize = new Size(GamesMinWindowWidth, GamesMinWindowHeight);
        StartPosition = FormStartPosition.CenterScreen;
        var appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (appIcon != null)
        {
            Icon = appIcon;
        }

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

        _splitterDebounceTimer.Interval = 150;
        _splitterDebounceTimer.Tick += (_, _) =>
        {
            _splitterDebounceTimer.Stop();
            EnsureSplitterValid();
        };

        Shown += (_, _) =>
        {
            StartRefresh();
            BeginInvoke(new Action(ApplyGamesSplitter));
        };
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
            QueueGamesRefresh("inventory_updated");
        };

        FormClosing += OnFormClosing;
        FormClosed += OnFormClosed;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _service.LogGamesRefresh("UI handle created.");
        if (_deferredInventoryRefresh)
        {
            _deferredInventoryRefresh = false;
            _service.LogGamesRefresh("UI handle created; starting deferred inventory refresh.");
            QueueGamesRefresh("deferred_inventory_refresh");
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _splitterDebounceTimer.Stop();
        _splitterDebounceTimer.Dispose();
        base.OnFormClosed(e);
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
            AutoSize = true,
            WrapContents = false
        };

        _launchOnSelectedAgentButton.Text = "Launch selected game";
        _launchOnSelectedAgentButton.Height = 32;
        _launchOnSelectedAgentButton.Enabled = false;
        _launchOnSelectedAgentButton.Click += async (_, _) => await LaunchSelectedGameOnAgentFromMenu();

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

        _updateSelectedButton.Text = "Update selected PC";
        _updateSelectedButton.Height = 32;
        _updateSelectedButton.Enabled = false;
        _updateSelectedButton.Click += (_, _) => UpdateSelectedAgent();

        _updateAllButton.Text = "Update all online PCs";
        _updateAllButton.Height = 32;
        _updateAllButton.Enabled = true;
        _updateAllButton.Click += (_, _) => UpdateAllAgents();
        _toolTip.SetToolTip(_updateAllButton, "Send update command to all online PCs");

        actions.Controls.AddRange(new Control[]
        {
            _launchOnSelectedAgentButton,
            _testButton,
            _testMissingButton,
            _shutdownButton,
            _updateSelectedButton,
            _updateAllButton
        });

        panel.Controls.Add(actions, 0, 0);
        panel.Controls.Add(BuildAgentsGrid(), 0, 1);
        return panel;
    }

    private Control BuildGamesTab()
    {
        try
        {
            _gamesSplit.Dock = DockStyle.Fill;
            _gamesSplit.Orientation = Orientation.Vertical;
            _gamesSplit.SplitterWidth = GamesSplitterWidth;
            _gamesSplit.IsSplitterFixed = false;
            _gamesSplit.Panel2MinSize = LaunchTargetsMinWidth;
            _gamesSplit.Panel1MinSize = GamesListMinWidth;
            _gamesSplit.Panel1.Padding = new Padding(8);
            _gamesSplit.Panel2.Padding = new Padding(8);
            _gamesSplit.SizeChanged += (_, _) => StartSplitterDebounce();
            _gamesSplit.SplitterMoved += (_, _) => StartSplitterDebounce();

            var leftPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4
            };
            leftPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            leftPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            leftPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false
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

        _requireAllToggle.Text = "Require on ALL checked PCs";
        _requireAllToggle.AutoSize = true;
        _requireAllToggle.CheckedChanged += (_, _) =>
        {
            _gamesDirty = true;
            RefreshGamesGridSafe();
        };
        _requireAllToggle.Checked = true;
        _requireAllToggle.Visible = false;

        _launchAllOnlineButton.Text = "Launch All Online";
        _launchAllOnlineButton.Height = 32;
        _launchAllOnlineButton.Enabled = false;
        _launchAllOnlineButton.Click += async (_, _) => await LaunchAllOnlineTargets();

        _launchSelectedButton.Text = "Launch Selected";
        _launchSelectedButton.Height = 32;
        _launchSelectedButton.Enabled = false;
        _launchSelectedButton.Click += async (_, _) => await LaunchSelectedTargets();

        _restartSteamButton.Text = "Restart Steam (Force Login)";
        _restartSteamButton.Height = 32;
        _restartSteamButton.Enabled = false;
        _restartSteamButton.Click += async (_, _) => await RestartSteamTargets();

        actions.Controls.AddRange(new Control[]
        {
            _refreshGamesButton,
            _showAllGamesToggle,
            _requireAllToggle
        });

        var filterGroup = new GroupBox
        {
            Text = "Filter by PC availability",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 48),
            Padding = new Padding(8)
        };
        _filterPanel.FlowDirection = FlowDirection.LeftToRight;
        _filterPanel.AutoSize = true;
        _filterPanel.Dock = DockStyle.Fill;
        filterGroup.Controls.Add(_filterPanel);

        _filterSummaryLabel.AutoSize = true;
        _filterSummaryLabel.Dock = DockStyle.Fill;
        _filterSummaryLabel.ForeColor = SystemColors.GrayText;

            var launchGroup = new GroupBox
            {
                Text = "Launch targets",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(LaunchTargetsMinWidth, 72),
                Padding = new Padding(8)
            };

        var launchLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        launchLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        launchLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _targetPanel.FlowDirection = FlowDirection.LeftToRight;
        _targetPanel.AutoSize = true;
        _targetPanel.Dock = DockStyle.Fill;

        var launchButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true
        };

        _selectAllOnlineButton.Text = "Select all online";
        _selectAllOnlineButton.Height = 32;
        _selectAllOnlineButton.Enabled = false;
        _selectAllOnlineButton.Click += (_, _) => SelectAllOnlineTargets();

        _clearTargetsButton.Text = "Clear";
        _clearTargetsButton.Height = 32;
        _clearTargetsButton.Enabled = false;
        _clearTargetsButton.Click += (_, _) => ClearTargetSelection();

        launchButtons.Controls.AddRange(new Control[]
        {
            _launchAllOnlineButton,
            _launchSelectedButton,
            _restartSteamButton,
            _selectAllOnlineButton,
            _clearTargetsButton
        });

        launchLayout.Controls.Add(_targetPanel, 0, 0);
        launchLayout.Controls.Add(launchButtons, 0, 1);
        launchGroup.Controls.Add(launchLayout);

        leftPanel.Controls.Add(actions, 0, 0);
        leftPanel.Controls.Add(filterGroup, 0, 1);
        leftPanel.Controls.Add(_filterSummaryLabel, 0, 2);
        leftPanel.Controls.Add(BuildGamesGrid(), 0, 3);

            _gamesSplit.Panel1.Controls.Add(leftPanel);
            _gamesSplit.Panel2.Controls.Add(launchGroup);
            return _gamesSplit;
        }
        catch (Exception ex)
        {
            _service.LogGamesRefresh($"BuildGamesTab failed: {ex.Message}");
            return BuildGamesFallback(ex.Message);
        }
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
        _agentsGrid.CellContentClick += AgentsGridCellContentClick;
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
        _menuLaunch.Click += async (_, _) => await LaunchSelectedGameOnAgentFromMenu();
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
        _gamesGrid.ReadOnly = true;
        _gamesGrid.AllowUserToAddRows = false;
        _gamesGrid.AllowUserToDeleteRows = false;
        _gamesGrid.RowHeadersVisible = false;
        _gamesGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gamesGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _gamesGrid.MultiSelect = false;
        _gamesGrid.SelectionChanged += (_, _) =>
        {
            if (_isRefreshingGames)
            {
                return;
            }

            LogGameSelectionIfChanged();
            UpdateTargetControlsForSelection();
            UpdateGameActions();
        };

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
            var availableVersion = _service.GetAvailableVersion();
            var normalizedAvailable = string.IsNullOrWhiteSpace(availableVersion)
                ? ""
                : VersionUtil.Normalize(availableVersion);
            _agentsGrid.Rows.Clear();

            foreach (var agent in snapshot)
            {
                var onlineText = FormatOnline(agent.Online, agent.LastSeen);
                var commandStatus = FormatCommandStatus(agent.LastStatus);
                var ackText = FormatAck(agent.LastAckTs, agent.LastAckOk, agent.LastAckError);
                var resultText = string.IsNullOrWhiteSpace(agent.LastResult) ? "-" : agent.LastResult;
                var lastError = agent.LastError ?? "";
                var truncatedError = Truncate(lastError, 60);
                var version = string.IsNullOrWhiteSpace(agent.Version) ? "-" : VersionUtil.Normalize(agent.Version);
                var availableDisplay = string.IsNullOrWhiteSpace(normalizedAvailable) ? "-" : normalizedAvailable;
                var decision = "Unknown";
                if (!string.IsNullOrWhiteSpace(version) && version != "-" &&
                    !string.IsNullOrWhiteSpace(normalizedAvailable))
                {
                    decision = VersionUtil.Compare(normalizedAvailable, version) > 0
                        ? "Update available"
                        : "Up-to-date";
                }
                var updateStatus = FormatUpdateStatus(agent.UpdateStatus);
                var canReset = agent.Online ||
                               agent.UpdateDisabled ||
                               agent.UpdateConsecutiveFailures > 0 ||
                               (!string.IsNullOrWhiteSpace(agent.UpdateMessage) &&
                                agent.UpdateMessage.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) >= 0);

                _agentsGrid.Rows.Add(
                    agent.PcId,
                    agent.Ip,
                    agent.Name,
                    onlineText,
                    version,
                    availableDisplay,
                    decision,
                    commandStatus,
                    updateStatus,
                    ackText,
                    resultText,
                    truncatedError,
                    "Reset Update Failures"
                );

                var row = _agentsGrid.Rows[^1];
                row.Cells["error"].ToolTipText = lastError;
                row.Cells["update"].ToolTipText = agent.UpdateMessage ?? "";
                row.Cells["reset"].Tag = canReset;
                if (!canReset)
                {
                    row.Cells["reset"].Style.ForeColor = SystemColors.GrayText;
                    row.Cells["reset"].Style.BackColor = SystemColors.ControlLight;
                }
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
            UpdateLaunchProgress(snapshot);
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

        try
        {
            _isRefreshingGames = true;
            // Games tab targets + availability filtering are driven by LeaderForm using LeaderService snapshots.
            var selectedAppId = _lastSelectedGameAppId ?? GetSelectedGameAppId();
            _service.LogGamesRefresh($"Games refresh begin selectedAppId={(selectedAppId?.ToString() ?? "-")}");

            var leaderCatalog = _service.GetLeaderCatalog();
            var agentInventories = _service.GetAgentInventoriesSnapshot();
            var inventoryErrors = _service.GetAgentInventoryErrorsSnapshot();
            var allAgents = _service.GetAgentsSnapshot()
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var remoteAgents = allAgents
                .Where(a => !_service.IsLocalAgent(a.PcId, a.Ip))
                .ToList();
            var targetAgents = BuildTargetAgents(allAgents);
            var localAgent = targetAgents.FirstOrDefault(agent => _service.IsLocalAgent(agent.PcId, agent.Ip));

            EnsureGamesGridColumns(remoteAgents);
            UpdateFilterControls(remoteAgents, inventoryErrors);

            var inventorySets = BuildInventorySets(agentInventories);
            EnsureLocalInventorySet(inventorySets, localAgent, leaderCatalog);
            var filtered = ApplyGameFilters(leaderCatalog, inventorySets, inventoryErrors, remoteAgents);
            UpdateFilterSummary(remoteAgents);

            _gamesGrid.Rows.Clear();
            foreach (var game in filtered)
            {
                var name = string.IsNullOrWhiteSpace(game.Name) ? $"App {game.AppId}" : game.Name;
                var rowIndex = _gamesGrid.Rows.Add(game.AppId, name);
                var row = _gamesGrid.Rows[rowIndex];

                foreach (var agent in remoteAgents)
                {
                    if (!_gamePcColumnMap.TryGetValue(agent.PcId, out var columnName))
                    {
                        continue;
                    }

                    var cell = row.Cells[columnName];
                    var hasError = inventoryErrors.TryGetValue(agent.PcId, out var error);
                    var installed = inventorySets.TryGetValue(agent.PcId, out var set) && set.Contains(game.AppId);

                    if (hasError)
                    {
                        cell.Value = "Err";
                        cell.Style.ForeColor = SystemColors.GrayText;
                        cell.Style.BackColor = SystemColors.Control;
                        cell.ToolTipText = error;
                    }
                    else if (installed)
                    {
                        cell.Value = "Yes";
                        cell.Style.ForeColor = SystemColors.WindowText;
                        cell.Style.BackColor = SystemColors.Window;
                        cell.ToolTipText = "Installed";
                    }
                    else
                    {
                        cell.Value = "No";
                        cell.Style.ForeColor = SystemColors.GrayText;
                        cell.Style.BackColor = SystemColors.Control;
                        cell.ToolTipText = "Not installed";
                    }
                }
            }

            _gamesGrid.ClearSelection();
            var selectionRestored = false;
            if (selectedAppId.HasValue)
            {
                foreach (DataGridViewRow row in _gamesGrid.Rows)
                {
                    if (row.Cells["appId"].Value is int appId && appId == selectedAppId.Value)
                    {
                        row.Selected = true;
                        selectionRestored = true;
                        break;
                    }
                }
            }
            if (!selectionRestored && !selectedAppId.HasValue && _gamesGrid.Rows.Count > 0)
            {
                _gamesGrid.Rows[0].Selected = true;
            }
            else if (!selectionRestored && selectedAppId.HasValue)
            {
                _lastSelectedGameAppId = null;
                _service.LogGamesRefresh($"Game selection cleared (missing after refresh) appId={selectedAppId.Value}");
            }
            else if (selectionRestored && selectedAppId.HasValue)
            {
                _lastSelectedGameAppId = selectedAppId.Value;
            }

            UpdateTargetControls(targetAgents, inventorySets, inventoryErrors);
            _gamesDirty = false;
            UpdateGameActions();
            _service.LogGamesUiState(filtered.Count, _selectedTargets.Count, targetAgents.Count);
            var selectedAfter = GetSelectedGameAppId();
            LogRefreshSelectionState(selectedAppId, selectedAfter);
            _service.LogGamesRefresh($"Games refresh end selectedAppId={(selectedAfter?.ToString() ?? "-")}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Games grid refresh failed: {ex}");
            _statusLabel.Text = $"Games refresh error: {ex.Message}";
        }
        finally
        {
            _isRefreshingGames = false;
        }
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
        _agentsGrid.Columns.Add("version", "Version");
        _agentsGrid.Columns.Add("available", "Available");
        _agentsGrid.Columns.Add("decision", "Update Decision");
        _agentsGrid.Columns.Add("command", "Command Status");
        _agentsGrid.Columns.Add("update", "Update Status");
        _agentsGrid.Columns.Add("ack", "Ack");
        _agentsGrid.Columns.Add("result", "Last Result");
        _agentsGrid.Columns.Add("error", "Last Error");
        var resetColumn = new DataGridViewButtonColumn
        {
            Name = "reset",
            HeaderText = "Reset Update Failures",
            Text = "Reset Update Failures",
            UseColumnTextForButtonValue = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells
        };
        _agentsGrid.Columns.Add(resetColumn);
    }

    private void EnsureGamesGridColumns(IEnumerable<AgentInfo> remoteAgents)
    {
        if (_gamesGrid.Columns.Count == 0)
        {
            var appIdCol = _gamesGrid.Columns.Add("appId", "App Id");
            _gamesGrid.Columns[appIdCol].Visible = false;
            _gamesGrid.Columns.Add("name", "Game");
        }

        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _gamePcColumnMap.Clear();

        foreach (var agent in remoteAgents)
        {
            var columnName = $"pc_{agent.PcId}";
            expected.Add(columnName);
            if (!_gamesGrid.Columns.Contains(columnName))
            {
                var column = new DataGridViewTextBoxColumn
                {
                    Name = columnName,
                    HeaderText = agent.Name,
                    ReadOnly = true,
                    AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells
                };
                column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                _gamesGrid.Columns.Add(column);
            }
            else
            {
                _gamesGrid.Columns[columnName].HeaderText = agent.Name;
            }
            _gamePcColumnMap[agent.PcId] = columnName;
        }

        var toRemove = new List<DataGridViewColumn>();
        foreach (DataGridViewColumn column in _gamesGrid.Columns)
        {
            if (column.Name.StartsWith("pc_", StringComparison.OrdinalIgnoreCase) &&
                !expected.Contains(column.Name))
            {
                toRemove.Add(column);
            }
        }

        foreach (var column in toRemove)
        {
            _gamesGrid.Columns.Remove(column);
        }
    }

    private void RefreshGames()
    {
        _gamesDirty = true;
        Task.Run(() => _service.RefreshSteamInventory());
    }

    private async Task LaunchSelectedTargets()
    {
        var selection = GetSelectedGame();
        if (selection == null)
        {
            MessageBox.Show("Select a game row first.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var checkedPcs = GetCheckedTargetPcIds();
        _service.LogLaunchRequest(selection.Value.AppId, selection.Value.Name, checkedPcs);
        if (checkedPcs.Count == 0)
        {
            MessageBox.Show("Select at least one target PC for this game.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _statusLabel.Text = "Launch failed: no target PCs selected.";
            return;
        }

        _launchSelectedButton.Enabled = false;
        _launchAllOnlineButton.Enabled = false;
        _statusLabel.Text = $"Launching {selection.Value.Name} on {checkedPcs.Count} PC(s)...";

        var (successes, failures) = await LaunchOnTargets(selection.Value.AppId, checkedPcs);

        if (successes.Count == 0)
        {
            var message = "Launch failed: no targets accepted the command.";
            if (failures.Count > 0)
            {
                message = $"Launch failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}";
            }

            MessageBox.Show(message, "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _statusLabel.Text = "Launch failed: no targets accepted the command.";
            UpdateGameActions();
            return;
        }

        _pendingLaunchTargets = new HashSet<string>(successes, StringComparer.OrdinalIgnoreCase);
        _pendingLaunchAppId = selection.Value.AppId;
        _statusLabel.Text = $"Launching {selection.Value.Name} on {successes.Count} PC(s)...";
        UpdateGameActions();
        RefreshAgentsGridSafe();

        if (failures.Count > 0)
        {
            MessageBox.Show(
                $"Some targets failed to accept the command:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}",
                "DadBoard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private async Task LaunchAllOnlineTargets()
    {
        var selection = GetSelectedGame();
        if (selection == null)
        {
            MessageBox.Show("Select a game row first.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var onlineTargets = GetEligibleOnlineTargetPcIds();
        _service.LogLaunchRequest(selection.Value.AppId, selection.Value.Name, onlineTargets);
        if (onlineTargets.Count == 0)
        {
            MessageBox.Show("No online targets available for this game.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _statusLabel.Text = "Launch failed: no online targets available.";
            return;
        }

        _launchSelectedButton.Enabled = false;
        _launchAllOnlineButton.Enabled = false;
        _statusLabel.Text = $"Launching {selection.Value.Name} on {onlineTargets.Count} PC(s)...";

        var (successes, failures) = await LaunchOnTargets(selection.Value.AppId, onlineTargets);
        if (successes.Count == 0)
        {
            var message = "Launch failed: no targets accepted the command.";
            if (failures.Count > 0)
            {
                message = $"Launch failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}";
            }

            MessageBox.Show(message, "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _statusLabel.Text = "Launch failed: no targets accepted the command.";
            UpdateGameActions();
            return;
        }

        _pendingLaunchTargets = new HashSet<string>(successes, StringComparer.OrdinalIgnoreCase);
        _pendingLaunchAppId = selection.Value.AppId;
        _statusLabel.Text = $"Launching {selection.Value.Name} on {successes.Count} PC(s)...";
        UpdateGameActions();
        RefreshAgentsGridSafe();

        if (failures.Count > 0)
        {
            MessageBox.Show(
                $"Some targets failed to accept the command:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}",
                "DadBoard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void SelectAllOnlineTargets()
    {
        var selection = GetSelectedGame();
        if (selection == null)
        {
            MessageBox.Show("Select a game row first.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _suppressTargetEvents = true;
        foreach (var entry in _targetCheckboxes)
        {
            if (entry.Value.Enabled)
            {
                entry.Value.Checked = true;
                _selectedTargets.Add(entry.Key);
            }
        }

        _suppressTargetEvents = false;
        LogTargetSelection();
        UpdateGameActions();
    }

    private void ClearTargetSelection()
    {
        _suppressTargetEvents = true;
        foreach (var checkbox in _targetCheckboxes.Values)
        {
            checkbox.Checked = false;
        }

        _selectedTargets.Clear();
        _suppressTargetEvents = false;
        LogTargetSelection();
        UpdateGameActions();
    }

    private async Task LaunchSelectedGameOnAgentFromMenu()
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

        var result = await _service.LaunchAppIdOnAgentAsync(selection.Value.AppId, pcId);
        if (!result.Ok)
        {
            MessageBox.Show(result.Error ?? "Unable to launch on selected PC.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _statusLabel.Text = $"Launching {selection.Value.Name} on {pcId}...";
    }

    private void LogGameSelectionIfChanged()
    {
        var selection = GetSelectedGame();
        var appId = selection?.AppId;
        if (appId == _lastSelectedGameAppId)
        {
            return;
        }

        _lastSelectedGameAppId = appId;
        if (selection != null)
        {
            _service.LogGameSelection(selection.Value.AppId, selection.Value.Name);
            _statusLabel.Text = $"Selected game: {selection.Value.Name}";
        }
        else
        {
            _service.LogGamesRefresh("Game selection cleared by user or dataset.");
        }
    }

    private void LogRefreshSelectionState(int? before, int? after)
    {
        var beforeText = before?.ToString() ?? "-";
        var afterText = after?.ToString() ?? "-";
        _service.LogGamesRefresh($"Games refresh selection before={beforeText} after={afterText}");
    }

    private Control BuildGamesFallback(string message)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(16)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            AutoSize = true,
            Text = $"Games UI failed to load. See app.log. Error: {message}"
        };

        var retryButton = new Button
        {
            Text = "Retry Games UI",
            AutoSize = true
        };
        retryButton.Click += (_, _) =>
        {
            _gamesTab.Controls.Clear();
            _gamesTab.Controls.Add(BuildGamesTab());
            ApplyGamesSplitter();
        };

        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(retryButton, 0, 1);
        return panel;
    }

    private int ClampSplitterDistance(SplitContainer sc, int desired)
    {
        var width = sc.ClientSize.Width;
        var min = sc.Panel1MinSize;
        var max = width - sc.Panel2MinSize;
        if (width <= 0 || max <= min)
        {
            LogSplitterWarn(width, min, max, desired, sc.SplitterDistance);
            return desired;
        }

        var clamped = Math.Min(Math.Max(desired, min), max);
        LogSplitterInfo(width, min, max, desired, clamped);
        return clamped;
    }

    private void ApplyGamesSplitter()
    {
        if (_gamesSplit.IsDisposed)
        {
            return;
        }

        if (_gamesSplit.ClientSize.Width <= 0)
        {
            LogSplitterWarn(_gamesSplit.ClientSize.Width, _gamesSplit.Panel1MinSize, _gamesSplit.ClientSize.Width - _gamesSplit.Panel2MinSize, _gamesSplit.SplitterDistance, _gamesSplit.SplitterDistance);
            return;
        }

        var desired = _gamesSplit.ClientSize.Width - _gamesSplit.Panel2MinSize;
        var clamped = ClampSplitterDistance(_gamesSplit, desired);
        if (_gamesSplit.SplitterDistance != clamped)
        {
            try
            {
                _gamesSplit.SplitterDistance = clamped;
            }
            catch (Exception ex)
            {
                _service.LogGamesRefresh($"ApplyGamesSplitter failed: {ex.Message}");
            }
        }
    }

    private void EnsureSplitterValid()
    {
        if (_gamesSplit.IsDisposed)
        {
            return;
        }

        if (_gamesSplit.ClientSize.Width <= 0)
        {
            return;
        }

        var desired = _gamesSplit.SplitterDistance;
        var clamped = ClampSplitterDistance(_gamesSplit, desired);
        if (clamped != desired && _gamesSplit.ClientSize.Width > 0)
        {
            try
            {
                _gamesSplit.SplitterDistance = clamped;
                _service.LogGamesRefresh($"SplitContainer distance corrected to {clamped}.");
            }
            catch (Exception ex)
            {
                _service.LogGamesRefresh($"EnsureSplitterValid failed: {ex.Message}");
            }
        }
    }

    private void StartSplitterDebounce()
    {
        if (_gamesSplit.IsDisposed)
        {
            return;
        }

        _splitterDebounceTimer.Stop();
        _splitterDebounceTimer.Start();
    }

    private void LogSplitterWarn(int width, int min, int max, int desired, int current)
    {
        if ((DateTime.UtcNow - _lastSplitterWarn).TotalSeconds < 5 &&
            width == _lastSplitterWarnWidth &&
            min == _lastSplitterWarnMin &&
            max == _lastSplitterWarnMax)
        {
            return;
        }

        _lastSplitterWarn = DateTime.UtcNow;
        _lastSplitterWarnWidth = width;
        _lastSplitterWarnMin = min;
        _lastSplitterWarnMax = max;
        _service.LogGamesRefresh($"WARN SplitContainer invalid width={width} min={min} max={max} desired={desired} current={current}");
    }

    private void LogSplitterInfo(int width, int min, int max, int desired, int clamped)
    {
        if ((DateTime.UtcNow - _lastSplitterInfo).TotalSeconds < 5)
        {
            return;
        }

        _lastSplitterInfo = DateTime.UtcNow;
        _service.LogGamesRefresh($"SplitContainer width={width} desired={desired} min={min} max={max} clamped={clamped}");
    }

    private void QueueGamesRefresh(string reason)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (!IsHandleCreated)
        {
            _deferredInventoryRefresh = true;
            _service.LogGamesRefresh($"Deferred games refresh (handle not created) reason={reason}");
            return;
        }

        BeginInvoke(new Action(() =>
        {
            _gamesDirty = true;
            if (_tabs.SelectedTab == _gamesTab)
            {
                RefreshGamesGridSafe();
            }
            else
            {
                _service.LogGamesRefresh($"Games refresh queued (tab inactive) reason={reason}");
            }
        }));
    }

    private void LogTargetSelection()
    {
        _service.LogTargetSelection(_selectedTargets);
        if (_selectedTargets.Count == 0)
        {
            _statusLabel.Text = "Targets cleared.";
        }
        else
        {
            _statusLabel.Text = $"Targets selected: {_selectedTargets.Count}";
        }
    }

    private async Task<(List<string> Successes, List<string> Failures)> LaunchOnTargets(int appId, List<string> targets)
    {
        var failures = new List<string>();
        var successes = new List<string>();
        foreach (var pcId in targets)
        {
            var result = await _service.LaunchAppIdOnAgentAsync(appId, pcId);
            if (!result.Ok)
            {
                failures.Add($"{pcId}: {result.Error ?? "Unknown error"}");
                continue;
            }

            successes.Add(pcId);
        }

        return (successes, failures);
    }

    private async Task RestartSteamTargets()
    {
        var targets = GetCheckedTargetPcIds();
        if (targets.Count == 0)
        {
            var onlineTargets = GetEligibleOnlineTargetPcIds();
            if (onlineTargets.Count == 0)
            {
                MessageBox.Show("No eligible targets are online.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var confirm = MessageBox.Show(
                "No targets selected. Restart Steam on ALL eligible targets?",
                "DadBoard",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            targets = onlineTargets;
        }

        _service.LogSteamRestartRequest(targets, selected: _selectedTargets.Count > 0);
        _statusLabel.Text = $"Restarting Steam on {targets.Count} PC(s)...";

        var (successes, failures) = await RestartSteamOnTargets(targets);
        if (successes.Count == 0 && failures.Count > 0)
        {
            var message = $"Restart Steam failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}";
            _statusLabel.Text = message;
            MessageBox.Show(message, "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _statusLabel.Text = $"Restarting Steam on {successes.Count} PC(s)...";
    }

    private async Task<(List<string> Successes, List<string> Failures)> RestartSteamOnTargets(List<string> targets)
    {
        var failures = new List<string>();
        var successes = new List<string>();
        foreach (var pcId in targets)
        {
            var result = await _service.RestartSteamOnAgentAsync(pcId);
            if (!result.Ok)
            {
                failures.Add($"{pcId}: {result.Error ?? "Unknown error"}");
                continue;
            }

            successes.Add(pcId);
        }

        return (successes, failures);
    }

    private void UpdateFilterSummary(List<AgentInfo> remoteAgents)
    {
        var checkedPcIds = _filterCheckboxes
            .Where(kv => kv.Value.Checked)
            .Select(kv => kv.Key)
            .ToList();
        if (checkedPcIds.Count == 0)
        {
            _filterSummaryLabel.Text = $"Filter: default (games on any remote PC, total {remoteAgents.Count})";
            return;
        }

        _filterSummaryLabel.Text = $"Filter: Available on ALL selected PCs ({checkedPcIds.Count})";
    }

    private static string BuildTargetStatus(AgentInfo agent)
    {
        var status = agent.Online ? "Online" : "Offline";
        if (!string.IsNullOrWhiteSpace(agent.LastStatus))
        {
            status += $" | {FormatCommandStatus(agent.LastStatus)}";
        }

        if (!string.IsNullOrWhiteSpace(agent.LastError) &&
            !string.Equals(agent.LastError, "none", StringComparison.OrdinalIgnoreCase))
        {
            status += $" | Err: {Truncate(agent.LastError, 40)}";
        }

        return status;
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

    private void UpdateSelectedAgent()
    {
        var agent = GetSelectedAgentInfo();
        if (agent == null)
        {
            MessageBox.Show("Select an agent row first.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_service.IsLocalAgent(agent.PcId, agent.Ip))
        {
            MessageBox.Show("Select a remote agent (not this PC).", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!_service.SendUpdateCommand(agent.PcId, out var error))
        {
            MessageBox.Show(error ?? "Unable to send update command.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _statusLabel.Text = $"Update requested for {agent.Name}.";
    }

    private void UpdateAllAgents()
    {
        _service.SendUpdateAllOnline();
        _statusLabel.Text = "Update requested for all online PCs.";
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

    private AgentInfo? GetSelectedAgentInfo()
    {
        var pcId = GetSelectedAgentPcId();
        if (string.IsNullOrWhiteSpace(pcId))
        {
            return null;
        }

        return _service.GetAgentsSnapshot()
            .FirstOrDefault(agent => string.Equals(agent.PcId, pcId, StringComparison.OrdinalIgnoreCase));
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

    private void UpdateTargetControlsForSelection()
    {
        var agentInventories = _service.GetAgentInventoriesSnapshot();
        var inventoryErrors = _service.GetAgentInventoryErrorsSnapshot();
        var allAgents = _service.GetAgentsSnapshot()
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var targetAgents = BuildTargetAgents(allAgents);
        var localAgent = targetAgents.FirstOrDefault(agent => _service.IsLocalAgent(agent.PcId, agent.Ip));

        var inventorySets = BuildInventorySets(agentInventories);
        EnsureLocalInventorySet(inventorySets, localAgent, _service.GetLeaderCatalog());
        UpdateTargetControls(targetAgents, inventorySets, inventoryErrors);
    }

    private static Dictionary<string, HashSet<int>> BuildInventorySets(IReadOnlyDictionary<string, GameInventory> inventories)
    {
        var map = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in inventories)
        {
            var set = new HashSet<int>();
            foreach (var game in entry.Value.Games)
            {
                if (game.AppId > 0)
                {
                    set.Add(game.AppId);
                }
            }

            map[entry.Key] = set;
        }

        return map;
    }

    private List<AgentInfo> BuildTargetAgents(List<AgentInfo> allAgents)
    {
        var local = allAgents.FirstOrDefault(agent => _service.IsLocalAgent(agent.PcId, agent.Ip));
        var targets = new List<AgentInfo>();
        if (local != null)
        {
            local.Name = $"{local.Name} (This PC)";
            targets.Add(local);
        }

        targets.AddRange(allAgents.Where(agent => !_service.IsLocalAgent(agent.PcId, agent.Ip)));
        return targets;
    }

    private static void EnsureLocalInventorySet(
        Dictionary<string, HashSet<int>> inventorySets,
        AgentInfo? localAgent,
        IReadOnlyList<SteamGameEntry> leaderCatalog)
    {
        if (localAgent == null || inventorySets.ContainsKey(localAgent.PcId))
        {
            return;
        }

        var set = new HashSet<int>();
        foreach (var game in leaderCatalog)
        {
            if (game.AppId > 0)
            {
                set.Add(game.AppId);
            }
        }

        inventorySets[localAgent.PcId] = set;
    }

    private List<SteamGameEntry> ApplyGameFilters(
        IReadOnlyList<SteamGameEntry> leaderCatalog,
        Dictionary<string, HashSet<int>> inventorySets,
        IReadOnlyDictionary<string, string> inventoryErrors,
        List<AgentInfo> remoteAgents)
    {
        var filterPcIds = _filterCheckboxes
            .Where(kv => kv.Value.Checked)
            .Select(kv => kv.Key)
            .ToList();

        IEnumerable<SteamGameEntry> games = leaderCatalog;

        if (filterPcIds.Count == 0)
        {
            if (!_showAllGamesToggle.Checked)
            {
                games = games.Where(game => remoteAgents.Any(agent =>
                    !inventoryErrors.ContainsKey(agent.PcId) &&
                    inventorySets.TryGetValue(agent.PcId, out var set) &&
                    set.Contains(game.AppId)));
            }
        }
        else
        {
        games = games.Where(game =>
            filterPcIds.All(pcId => !inventoryErrors.ContainsKey(pcId) &&
                                     inventorySets.TryGetValue(pcId, out var set) &&
                                     set.Contains(game.AppId)));
        }

        return games
            .OrderBy(g => g.Name ?? $"App {g.AppId}", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void UpdateFilterControls(List<AgentInfo> remoteAgents, IReadOnlyDictionary<string, string> inventoryErrors)
    {
        var keep = new HashSet<string>(remoteAgents.Select(a => a.PcId), StringComparer.OrdinalIgnoreCase);

        foreach (var pcId in _filterCheckboxes.Keys.Where(pcId => !keep.Contains(pcId)).ToList())
        {
            var checkbox = _filterCheckboxes[pcId];
            _filterPanel.Controls.Remove(checkbox);
            checkbox.Dispose();
            _filterCheckboxes.Remove(pcId);
        }

        foreach (var agent in remoteAgents)
        {
            if (!_filterCheckboxes.TryGetValue(agent.PcId, out var checkbox))
            {
                checkbox = new CheckBox
                {
                    AutoSize = true,
                    Text = agent.Name
                };
                checkbox.CheckedChanged += (_, _) =>
                {
                    _gamesDirty = true;
                    RefreshGamesGridSafe();
                };
                _filterPanel.Controls.Add(checkbox);
                _filterCheckboxes[agent.PcId] = checkbox;
            }
            else
            {
                checkbox.Text = agent.Name;
            }

            if (inventoryErrors.TryGetValue(agent.PcId, out var error))
            {
                if (!string.IsNullOrWhiteSpace(error) &&
                    !string.Equals(error, "none", StringComparison.OrdinalIgnoreCase))
                {
                    checkbox.Checked = false;
                    checkbox.Enabled = false;
                    _toolTip.SetToolTip(checkbox, error);
                    continue;
                }
            }

            checkbox.Enabled = true;
            _toolTip.SetToolTip(checkbox, "");
        }
    }

    private void UpdateTargetControls(
        List<AgentInfo> targetAgents,
        Dictionary<string, HashSet<int>> inventorySets,
        IReadOnlyDictionary<string, string> inventoryErrors)
    {
        var selected = GetSelectedGame();
        var selectedAppId = selected?.AppId;
        var keep = new HashSet<string>(targetAgents.Select(a => a.PcId), StringComparer.OrdinalIgnoreCase);
        _suppressTargetEvents = true;

        foreach (var pcId in _targetCheckboxes.Keys.Where(pcId => !keep.Contains(pcId)).ToList())
        {
            var checkbox = _targetCheckboxes[pcId];
            if (_targetRows.TryGetValue(pcId, out var row))
            {
                _targetPanel.Controls.Remove(row);
                row.Dispose();
                _targetRows.Remove(pcId);
            }
            else
            {
                _targetPanel.Controls.Remove(checkbox);
            }
            checkbox.Dispose();
            _targetCheckboxes.Remove(pcId);
            _targetStatusLabels.Remove(pcId);
            _targetEligibility.Remove(pcId);
            _selectedTargets.Remove(pcId);
        }

        foreach (var agent in targetAgents)
        {
            if (!_targetCheckboxes.TryGetValue(agent.PcId, out var checkbox))
            {
                var pcId = agent.PcId;
                checkbox = new CheckBox
                {
                    AutoSize = true,
                    Text = agent.Name
                };
                checkbox.CheckedChanged += (_, _) =>
                {
                    if (_suppressTargetEvents)
                    {
                        return;
                    }

                    if (checkbox.Checked)
                    {
                        _selectedTargets.Add(pcId);
                    }
                    else
                    {
                        _selectedTargets.Remove(pcId);
                    }

                    LogTargetSelection();
                    UpdateGameActions();
                };

                var statusLabel = new Label
                {
                    AutoSize = true,
                    ForeColor = SystemColors.GrayText
                };

                var row = new FlowLayoutPanel
                {
                    AutoSize = true,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false
                };
                row.Controls.Add(checkbox);
                row.Controls.Add(statusLabel);
                _targetPanel.Controls.Add(row);
                _targetCheckboxes[agent.PcId] = checkbox;
                _targetStatusLabels[pcId] = statusLabel;
                _targetRows[pcId] = row;
            }
            else
            {
                checkbox.Text = agent.Name;
            }

            if (_targetStatusLabels.TryGetValue(agent.PcId, out var label))
            {
                label.Text = BuildTargetStatus(agent);
                label.ForeColor = agent.Online ? SystemColors.WindowText : SystemColors.GrayText;
                var lastError = string.Equals(agent.LastError, "none", StringComparison.OrdinalIgnoreCase) ? "" : agent.LastError;
                _toolTip.SetToolTip(label, lastError ?? "");
            }

        if (!selectedAppId.HasValue)
        {
            checkbox.Checked = _selectedTargets.Contains(agent.PcId);
            if (!agent.Online)
            {
                SetTargetEligibility(agent, checkbox, enabled: false, reason: "PC is offline.");
            }
            else
            {
                SetTargetEligibility(agent, checkbox, enabled: true, reason: "Select a game to launch. Steam restart available.");
            }
            continue;
        }

            if (inventoryErrors.TryGetValue(agent.PcId, out var error) &&
                !string.IsNullOrWhiteSpace(error) &&
                !string.Equals(error, "none", StringComparison.OrdinalIgnoreCase))
            {
                checkbox.Checked = _selectedTargets.Contains(agent.PcId);
                SetTargetEligibility(agent, checkbox, enabled: false, reason: error);
                continue;
            }

            var hasInventory = inventorySets.TryGetValue(agent.PcId, out var set);
            var installed = hasInventory && set!.Contains(selectedAppId.Value);
            if (hasInventory && !installed)
            {
                checkbox.Checked = _selectedTargets.Contains(agent.PcId);
                SetTargetEligibility(agent, checkbox, enabled: false, reason: "Not installed on this PC.");
                continue;
            }

            if (!agent.Online)
            {
                checkbox.Checked = _selectedTargets.Contains(agent.PcId);
                SetTargetEligibility(agent, checkbox, enabled: false, reason: "PC is offline.");
                continue;
            }

            checkbox.Enabled = true;
            checkbox.Checked = _selectedTargets.Contains(agent.PcId);
            var reason = installed ? "Installed." : "Inventory not available; launch may fail.";
            SetTargetEligibility(agent, checkbox, enabled: true, reason: reason);
        }

        _suppressTargetEvents = false;
    }

    private void SetTargetEligibility(AgentInfo agent, CheckBox checkbox, bool enabled, string reason)
    {
        checkbox.Enabled = enabled;
        _toolTip.SetToolTip(checkbox, reason);
        _targetEligibility[agent.PcId] = enabled;
        _service.LogTargetEligibility(agent.PcId, agent.Name, enabled, reason);
    }

    private List<string> GetCheckedTargetPcIds()
    {
        return _selectedTargets
            .Where(pcId => _targetEligibility.TryGetValue(pcId, out var eligible) && eligible)
            .ToList();
    }

    private List<string> GetEligibleOnlineTargetPcIds()
    {
        return _targetEligibility
            .Where(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();
    }

    private void UpdateAgentActions()
    {
        if (_agentsGrid.SelectedRows.Count == 0)
        {
            _launchOnSelectedAgentButton.Enabled = false;
            _testButton.Enabled = false;
            _testMissingButton.Enabled = false;
            _shutdownButton.Enabled = false;
            _updateSelectedButton.Enabled = false;
            _updateAllButton.Enabled = _service.GetAgentsSnapshot().Any(a => a.Online && !_service.IsLocalAgent(a.PcId, a.Ip));
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
            _updateSelectedButton.Enabled = false;
            _updateAllButton.Enabled = _service.GetAgentsSnapshot().Any(a => a.Online && !_service.IsLocalAgent(a.PcId, a.Ip));
            return;
        }

        var enabled = !_service.IsLocalAgent(pcId, ip);
        var selectedAgent = GetSelectedAgentInfo();
        var online = selectedAgent?.Online ?? false;
        _launchOnSelectedAgentButton.Enabled = GetSelectedGame() != null;
        _testButton.Enabled = enabled;
        _testMissingButton.Enabled = enabled;
        _shutdownButton.Enabled = enabled;
        _updateSelectedButton.Enabled = enabled && online;
        _updateAllButton.Enabled = _service.GetAgentsSnapshot().Any(a => a.Online && !_service.IsLocalAgent(a.PcId, a.Ip));
    }

    private void UpdateGameActions()
    {
        var selection = GetSelectedGame();
        var reason = "";
        if (selection == null)
        {
            _launchAllOnlineButton.Enabled = false;
            _launchSelectedButton.Enabled = false;
            _selectAllOnlineButton.Enabled = false;
            _clearTargetsButton.Enabled = _selectedTargets.Count > 0;
            _restartSteamButton.Enabled = GetEligibleOnlineTargetPcIds().Count > 0 || GetCheckedTargetPcIds().Count > 0;
            _toolTip.SetToolTip(_launchAllOnlineButton, "Select a game to enable launch.");
            _toolTip.SetToolTip(_launchSelectedButton, "Select a game and at least one target PC.");
            _toolTip.SetToolTip(_restartSteamButton, _restartSteamButton.Enabled
                ? ""
                : "Select targets to restart Steam.");
            reason = "disabled: no game selected";
            UpdateAgentActions();
            LogLaunchButtonState(reason);
            return;
        }

        var checkedTargets = GetCheckedTargetPcIds();
        var onlineTargets = GetEligibleOnlineTargetPcIds();
        _launchAllOnlineButton.Enabled = onlineTargets.Count > 0 && _pendingLaunchTargets == null;
        _launchSelectedButton.Enabled = checkedTargets.Count > 0 && _pendingLaunchTargets == null;
        _selectAllOnlineButton.Enabled = _targetCheckboxes.Values.Any(cb => cb.Enabled);
        _clearTargetsButton.Enabled = _selectedTargets.Count > 0;
        _restartSteamButton.Enabled = (onlineTargets.Count > 0 || checkedTargets.Count > 0) && _pendingLaunchTargets == null;
        _toolTip.SetToolTip(_launchAllOnlineButton, _launchAllOnlineButton.Enabled ? "" : "Select a game and at least one online target PC.");
        _toolTip.SetToolTip(_launchSelectedButton, _launchSelectedButton.Enabled ? "" : "Select a game and at least one target PC.");
        _toolTip.SetToolTip(_restartSteamButton, _restartSteamButton.Enabled ? "" : "Select targets to restart Steam.");
        if (!_launchAllOnlineButton.Enabled && onlineTargets.Count == 0)
        {
            reason = "disabled: no online targets";
        }
        else if (!_launchSelectedButton.Enabled && checkedTargets.Count == 0)
        {
            reason = "disabled: no selected targets";
        }
        else
        {
            reason = "enabled";
        }
        UpdateAgentActions();
        LogLaunchButtonState(reason);
    }

    private void LogLaunchButtonState(string reason)
    {
        if (_lastLaunchAllEnabled == _launchAllOnlineButton.Enabled &&
            _lastLaunchSelectedEnabled == _launchSelectedButton.Enabled &&
            string.Equals(_lastLaunchReason, reason, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastLaunchAllEnabled = _launchAllOnlineButton.Enabled;
        _lastLaunchSelectedEnabled = _launchSelectedButton.Enabled;
        _lastLaunchReason = reason;
        _service.LogLaunchButtonsState(_launchAllOnlineButton.Enabled, _launchSelectedButton.Enabled, reason);
    }

    private void UpdateLaunchProgress(IReadOnlyList<AgentInfo> snapshot)
    {
        if (_pendingLaunchTargets == null || _pendingLaunchTargets.Count == 0)
        {
            return;
        }

        var targets = snapshot.Where(agent => _pendingLaunchTargets.Contains(agent.PcId)).ToList();
        if (targets.Count == 0)
        {
            _pendingLaunchTargets = null;
            _pendingLaunchAppId = null;
            UpdateGameActions();
            return;
        }

        var allDone = targets.All(agent => IsTerminalLaunchStatus(agent.LastStatus));
        if (allDone)
        {
            _pendingLaunchTargets = null;
            _pendingLaunchAppId = null;
            UpdateGameActions();
        }
    }

    private static bool IsTerminalLaunchStatus(string status)
    {
        return string.Equals(status, "running", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "timeout", StringComparison.OrdinalIgnoreCase);
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

    private void AgentsGridCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0)
        {
            return;
        }

        if (_agentsGrid.Columns[e.ColumnIndex].Name != "reset")
        {
            return;
        }

        var row = _agentsGrid.Rows[e.RowIndex];
        var canReset = row.Cells["reset"].Tag as bool? ?? false;
        if (!canReset)
        {
            MessageBox.Show(
                "Reset is available when the agent is online or has cached update failures.",
                "DadBoard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var pcId = row.Cells["pcId"].Value?.ToString();
        var name = row.Cells["name"].Value?.ToString() ?? "agent";
        if (string.IsNullOrWhiteSpace(pcId))
        {
            MessageBox.Show("Unable to identify agent.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"This clears the update circuit breaker and retry backoff for {name}. Continue?",
            "DadBoard",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        if (!_service.ResetUpdateFailures(pcId, "dashboard", out var error))
        {
            MessageBox.Show(error ?? "Reset failed.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _statusLabel.Text = $"Update failures reset for {name}. You can retry update now.";
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
            "launching" => "Launching",
            "steam_restart_starting" => "Restarting Steam",
            "steam_restart_completed" => "Steam restarted",
            "steam_restart_failed" => "Steam restart failed",
            "stopping" => "Running",
            _ => "Idle"
        };
    }

    private static string FormatUpdateStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return "Idle";
        }

        return status.ToLowerInvariant() switch
        {
            "requested" => "Requested",
            "starting_update" => "Starting",
            "starting" => "Starting",
            "downloading" => "Downloading",
            "installing" => "Installing",
            "applying" => "Applying",
            "restarting" => "Restarting",
            "updated" => "Updated",
            "failed" => "Failed",
            "sent" => "Sent",
            _ => status
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
