using System;
using System.Drawing;
using System.Windows.Forms;

namespace DadBoard.Gate;

sealed class GateTrayContext : ApplicationContext
{
    private readonly GateEngine _engine;
    private readonly NotifyIcon _tray;
    private readonly StatusForm _statusForm;
    private readonly ToolStripMenuItem _leaderItem;
    private readonly ToolStripMenuItem _coItem;
    private readonly ToolStripMenuItem _normalItem;
    private readonly ToolStripMenuItem _deviceMenu;
    private readonly ToolStripMenuItem _calibrateItem;
    private readonly ToolStripMenuItem _quickTestItem;
    private readonly ToolStripMenuItem _statusRole;
    private readonly ToolStripMenuItem _statusTalking;
    private readonly ToolStripMenuItem _statusAllowed;
    private readonly ToolStripMenuItem _statusMic;
    private readonly System.Windows.Forms.Timer _uiTimer;

    public GateTrayContext()
    {
        _engine = new GateEngine();
        _engine.Start();

        _statusForm = new StatusForm(_engine);

        _leaderItem = new ToolStripMenuItem("Make this PC Leader", null, (_, _) => _engine.ClaimRole(Role.Leader));
        _coItem = new ToolStripMenuItem("Make this PC Co-Captain", null, (_, _) => _engine.ClaimRole(Role.CoCaptain));
        _normalItem = new ToolStripMenuItem("Set Normal", null, (_, _) => _engine.ClaimRole(Role.Normal));

        _deviceMenu = new ToolStripMenuItem("Input Device");
        _deviceMenu.DropDownOpening += (_, _) => PopulateDevices();

        _calibrateItem = new ToolStripMenuItem("Calibrate Mic", null, async (_, _) => await RunCalibration());
        _quickTestItem = new ToolStripMenuItem("Quick Test (Gate 5%)", null, async (_, _) => await RunQuickTest());

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
            _deviceMenu,
            _calibrateItem,
            _quickTestItem,
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
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Visible = true,
            Text = "DadBoard Gate",
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

    private void PopulateDevices()
    {
        _deviceMenu.DropDownItems.Clear();
        var devices = _engine.GetInputDevices();
        var selectedId = _engine.GetSelectedDeviceId();

        if (devices.Count == 0)
        {
            _deviceMenu.DropDownItems.Add(new ToolStripMenuItem("No capture devices found") { Enabled = false });
            return;
        }

        foreach (var device in devices)
        {
            var item = new ToolStripMenuItem(device.Name) { Checked = string.Equals(device.Id, selectedId, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => _engine.SetInputDevice(device.Id);
            _deviceMenu.DropDownItems.Add(item);
        }
    }

    private async Task RunCalibration()
    {
        using var form = new MeterForm("Calibrate Mic", "Say 1–5 for 3 seconds.");
        form.Show();
        try
        {
            await _engine.CalibrateMicAsync(form, form.CancellationToken);
        }
        finally
        {
            form.Close();
        }
    }

    private async Task RunQuickTest()
    {
        using var form = new MeterForm("Quick Test", "Mic gated to 5%. Speak to verify VOX stays off.");
        form.Show();
        try
        {
            await _engine.QuickTestAsync(form, form.CancellationToken);
        }
        finally
        {
            form.Close();
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
