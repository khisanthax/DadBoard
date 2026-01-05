using System;
using System.Drawing;
using System.IO;
using System.Linq;
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

    private readonly AgentService _agent;
    private LeaderService? _leader;
    private LeaderForm? _leaderForm;
    private StatusForm? _statusForm;

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _enableLeaderItem;
    private readonly ToolStripMenuItem _disableLeaderItem;
    private readonly ToolStripMenuItem _startLeaderOnLoginItem;
    private readonly ToolStripMenuItem _statusItem;

    private AgentConfig _config;
    private bool _disposed;

    public TrayAppContext(string[] args)
    {
        _baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DadBoard");
        _agentConfigPath = Path.Combine(_baseDir, "Agent", "agent.config.json");
        _configStore = new AppConfigStore(_agentConfigPath);

        _agent = new AgentService();
        _agent.Start();

        _config = _configStore.Load();

        _enableLeaderItem = new ToolStripMenuItem("Enable Leader", null, (_, _) => EnableLeader(showUI: true));
        _disableLeaderItem = new ToolStripMenuItem("Disable Leader", null, (_, _) => DisableLeader());
        _startLeaderOnLoginItem = new ToolStripMenuItem("Start Leader on Login") { CheckOnClick = true };
        _startLeaderOnLoginItem.Checked = _config.StartLeaderOnLogin;
        _startLeaderOnLoginItem.CheckedChanged += (_, _) => ToggleStartLeaderOnLogin();

        _statusItem = new ToolStripMenuItem("Show Status", null, (_, _) => ShowStatus());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _enableLeaderItem,
            _disableLeaderItem,
            new ToolStripSeparator(),
            _startLeaderOnLoginItem,
            _statusItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => Exit())
        });

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "DadBoard",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowStatus();

        if (args.Any(arg => string.Equals(arg, "--enable-leader", StringComparison.OrdinalIgnoreCase)))
        {
            EnableLeader(showUI: true);
        }
        else if (_config.StartLeaderOnLogin)
        {
            EnableLeader(showUI: false);
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

        _leader = new LeaderService();
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

        _leaderForm?.Close();
        _leaderForm = null;

        _leader.Dispose();
        _leader = null;

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

    private void ToggleStartLeaderOnLogin()
    {
        _config.StartLeaderOnLogin = _startLeaderOnLoginItem.Checked;
        _configStore.Save(_config);
    }

    private void UpdateMenuState()
    {
        var leaderEnabled = _leader != null;
        _enableLeaderItem.Enabled = !leaderEnabled;
        _disableLeaderItem.Enabled = leaderEnabled;
    }

    private void Exit()
    {
        _tray.Visible = false;
        _leaderForm?.Close();
        _leader?.Dispose();
        Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _tray.Dispose();
            _leader?.Dispose();
            _agent.Dispose();
        }
        base.Dispose(disposing);
    }
}
