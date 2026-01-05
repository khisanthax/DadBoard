using System;
using System.Drawing;
using System.Windows.Forms;

namespace GateAgent;

sealed class GateAgentContext : ApplicationContext
{
    private readonly GateEngine _engine;
    private readonly NotifyIcon _tray;
    private readonly StatusForm _statusForm;
    private readonly ToolStripMenuItem _leaderItem;
    private readonly ToolStripMenuItem _coItem;
    private readonly ToolStripMenuItem _normalItem;
    private readonly ToolStripMenuItem _statusRole;
    private readonly ToolStripMenuItem _statusTalking;
    private readonly ToolStripMenuItem _statusAllowed;
    private readonly ToolStripMenuItem _statusMic;
    private readonly System.Windows.Forms.Timer _uiTimer;

    public GateAgentContext()
    {
        _engine = new GateEngine();
        _engine.Start();

        _statusForm = new StatusForm(_engine);

        _leaderItem = new ToolStripMenuItem("Make this PC Leader", null, (_, _) => _engine.ClaimRole(Role.Leader));
        _coItem = new ToolStripMenuItem("Make this PC Co-Captain", null, (_, _) => _engine.ClaimRole(Role.CoCaptain));
        _normalItem = new ToolStripMenuItem("Set Normal", null, (_, _) => _engine.ClaimRole(Role.Normal));

        _statusRole = new ToolStripMenuItem("Role: ") { Enabled = false };
        _statusTalking = new ToolStripMenuItem("Talking: ") { Enabled = false };
        _statusAllowed = new ToolStripMenuItem("Allowed: ") { Enabled = false };
        _statusMic = new ToolStripMenuItem("Mic: ") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _leaderItem,
            _coItem,
            _normalItem,
            new ToolStripSeparator(),
            _statusRole,
            _statusTalking,
            _statusAllowed,
            _statusMic,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open Status Window", null, (_, _) => ShowStatus()),
            new ToolStripMenuItem("Exit", null, (_, _) => Exit())
        });

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "DadBoard GateAgent",
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowStatus();

        _uiTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _uiTimer.Tick += (_, _) => UpdateUi();
        _uiTimer.Start();
    }

    private void ShowStatus()
    {
        if (_statusForm.Visible)
        {
            _statusForm.BringToFront();
            return;
        }

        _statusForm.Show();
    }

    private void UpdateUi()
    {
        var snapshot = _engine.GetSnapshot();

        _statusRole.Text = $"Role: {snapshot.EffectiveRole}";
        _statusTalking.Text = $"Talking: {(snapshot.Talking ? "Yes" : "No")}";
        _statusAllowed.Text = $"Allowed: {(snapshot.Allowed ? "Yes" : "No")}";
        _statusMic.Text = $"Mic: {snapshot.MicScalar:0.00}";

        _leaderItem.Checked = snapshot.EffectiveRole == Role.Leader;
        _coItem.Checked = snapshot.EffectiveRole == Role.CoCaptain;
        _normalItem.Checked = snapshot.EffectiveRole == Role.Normal;

        if (_statusForm.Visible)
        {
            _statusForm.ApplySnapshot(snapshot);
        }
    }

    private void Exit()
    {
        _uiTimer.Stop();
        _tray.Visible = false;
        _engine.Dispose();
        _tray.Dispose();
        _statusForm.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _uiTimer.Dispose();
            _tray.Dispose();
            _statusForm.Dispose();
            _engine.Dispose();
        }
        base.Dispose(disposing);
    }
}
