using System;
using System.Drawing;
using System.Windows.Forms;

namespace GateAgent;

sealed class StatusForm : Form
{
    private readonly GateEngine _engine;

    private readonly Label _roleValue = new();
    private readonly Label _talkingValue = new();
    private readonly Label _allowedValue = new();
    private readonly Label _micValue = new();
    private readonly Label _floorValue = new();
    private readonly Label _thresholdValue = new();
    private readonly TrackBar _sensitivitySlider = new();
    private readonly NumericUpDown _attackInput = new();
    private readonly NumericUpDown _releaseInput = new();
    private readonly NumericUpDown _leaseInput = new();
    private readonly DataGridView _grid = new();

    private bool _suppressUpdates;

    public StatusForm(GateEngine engine)
    {
        _engine = engine;
        Text = "DadBoard GateAgent";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(720, 500);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var statusPanel = BuildStatusPanel();
        layout.Controls.Add(statusPanel, 0, 0);

        ConfigureGrid();
        layout.Controls.Add(_grid, 0, 1);

        Controls.Add(layout);

        FormClosing += OnFormClosing;
        Load += OnLoad;
    }

    private void OnLoad(object? sender, EventArgs e)
    {
        var snapshot = _engine.GetSnapshot();
        ApplySnapshot(snapshot);
    }

    private Control BuildStatusPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            AutoSize = true
        };

        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));

        panel.Controls.Add(new Label { Text = "Role:", AutoSize = true }, 0, 0);
        panel.Controls.Add(_roleValue, 1, 0);
        panel.Controls.Add(new Label { Text = "Talking:", AutoSize = true }, 2, 0);
        panel.Controls.Add(_talkingValue, 3, 0);
        panel.Controls.Add(new Label { Text = "Allowed:", AutoSize = true }, 4, 0);
        panel.Controls.Add(_allowedValue, 5, 0);

        panel.Controls.Add(new Label { Text = "Mic Scalar:", AutoSize = true }, 0, 1);
        panel.Controls.Add(_micValue, 1, 1);
        panel.Controls.Add(new Label { Text = "Floor Owner:", AutoSize = true }, 2, 1);
        panel.Controls.Add(_floorValue, 3, 1);

        panel.Controls.Add(new Label { Text = "Sensitivity:", AutoSize = true }, 0, 2);
        panel.Controls.Add(_sensitivitySlider, 1, 2);
        panel.Controls.Add(_thresholdValue, 2, 2);

        _sensitivitySlider.Minimum = 1;
        _sensitivitySlider.Maximum = 200;
        _sensitivitySlider.TickFrequency = 20;
        _sensitivitySlider.AutoSize = false;
        _sensitivitySlider.Height = 24;
        _sensitivitySlider.ValueChanged += OnSensitivityChanged;

        var calibrateButton = new Button { Text = "Calibrate", AutoSize = true };
        calibrateButton.Click += (_, _) => CalibrateSensitivity();
        panel.Controls.Add(calibrateButton, 3, 2);

        panel.Controls.Add(new Label { Text = "Gate Level:", AutoSize = true }, 4, 2);
        panel.Controls.Add(new Label { Text = "5%", AutoSize = true }, 5, 2);

        panel.Controls.Add(new Label { Text = "Attack (ms):", AutoSize = true }, 0, 3);
        panel.Controls.Add(_attackInput, 1, 3);
        panel.Controls.Add(new Label { Text = "Release (ms):", AutoSize = true }, 2, 3);
        panel.Controls.Add(_releaseInput, 3, 3);
        panel.Controls.Add(new Label { Text = "Lease (ms):", AutoSize = true }, 4, 3);
        panel.Controls.Add(_leaseInput, 5, 3);

        ConfigureNumeric(_attackInput, 10, 1000, 50, OnTimingChanged);
        ConfigureNumeric(_releaseInput, 50, 2000, 300, OnTimingChanged);
        ConfigureNumeric(_leaseInput, 100, 2000, 600, OnTimingChanged);

        return panel;
    }

    private void ConfigureNumeric(NumericUpDown control, int min, int max, int value, EventHandler handler)
    {
        control.Minimum = min;
        control.Maximum = max;
        control.Value = value;
        control.ValueChanged += handler;
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add("pc", "PC");
        _grid.Columns.Add("role", "Role");
        _grid.Columns.Add("talking", "Talking");
        _grid.Columns.Add("lastSeen", "Last Seen");
    }

    public void ApplySnapshot(GateSnapshot snapshot)
    {
        _roleValue.Text = snapshot.EffectiveRole.ToString();
        _talkingValue.Text = snapshot.Talking ? "Yes" : "No";
        _allowedValue.Text = snapshot.Allowed ? "Allowed" : "Gated";
        _micValue.Text = snapshot.MicScalar.ToString("0.00");
        _floorValue.Text = string.IsNullOrWhiteSpace(snapshot.FloorOwner) ? "(none)" : snapshot.FloorOwner;

        UpdateSensitivityControls(snapshot);

        _grid.Rows.Clear();
        foreach (var peer in snapshot.Peers)
        {
            _grid.Rows.Add(
                peer.PcId,
                peer.EffectiveRole.ToString(),
                peer.Talking ? "Yes" : "No",
                peer.LastSeen.ToLocalTime().ToString("HH:mm:ss")
            );
        }
    }

    private void UpdateSensitivityControls(GateSnapshot snapshot)
    {
        var settings = _engine.GetSettingsCopy();
        _suppressUpdates = true;
        try
        {
            var sliderValue = Math.Clamp((int)Math.Round(settings.Sensitivity * 1000), _sensitivitySlider.Minimum, _sensitivitySlider.Maximum);
            _sensitivitySlider.Value = sliderValue;
            _thresholdValue.Text = settings.Sensitivity.ToString("0.000");

            _attackInput.Value = Math.Clamp(settings.AttackMs, (int)_attackInput.Minimum, (int)_attackInput.Maximum);
            _releaseInput.Value = Math.Clamp(settings.ReleaseMs, (int)_releaseInput.Minimum, (int)_releaseInput.Maximum);
            _leaseInput.Value = Math.Clamp(settings.LeaseMs, (int)_leaseInput.Minimum, (int)_leaseInput.Maximum);
        }
        finally
        {
            _suppressUpdates = false;
        }
    }

    private void OnSensitivityChanged(object? sender, EventArgs e)
    {
        if (_suppressUpdates)
        {
            return;
        }

        var threshold = _sensitivitySlider.Value / 1000.0;
        _thresholdValue.Text = threshold.ToString("0.000");
        _engine.UpdateSettings(s => s.Sensitivity = threshold);
    }

    private void OnTimingChanged(object? sender, EventArgs e)
    {
        if (_suppressUpdates)
        {
            return;
        }

        _engine.UpdateSettings(s =>
        {
            s.AttackMs = (int)_attackInput.Value;
            s.ReleaseMs = (int)_releaseInput.Value;
            s.LeaseMs = (int)_leaseInput.Value;
        });
    }

    private void CalibrateSensitivity()
    {
        var ambient = _engine.GetCurrentLevel();
        _engine.CalibrateSensitivity(ambient);
        var settings = _engine.GetSettingsCopy();
        _suppressUpdates = true;
        _sensitivitySlider.Value = Math.Clamp((int)Math.Round(settings.Sensitivity * 1000), _sensitivitySlider.Minimum, _sensitivitySlider.Maximum);
        _thresholdValue.Text = settings.Sensitivity.ToString("0.000");
        _suppressUpdates = false;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
