using System;
using System.Drawing;
using System.Windows.Forms;

namespace DadBoard.Gate;

sealed class StatusForm : Form
{
    private readonly GateEngine _engine;

    private readonly Label _roleValue = new();
    private readonly Label _talkingValue = new();
    private readonly Label _allowedValue = new();
    private readonly Label _micValue = new();
    private readonly Label _floorValue = new();
    private readonly Label _blockedValue = new();
    private readonly Label _leaderValue = new();
    private readonly Label _coValue = new();
    private readonly Label _peerCountValue = new();
    private readonly Label _lastPeerValue = new();
    private readonly Label _thresholdValue = new();
    private readonly Label _portValue = new();
    private readonly Label _gainValue = new();
    private readonly TrackBar _sensitivitySlider = new();
    private readonly TrackBar _gainSlider = new();
    private readonly CheckBox _autoGainCheck = new();
    private readonly NumericUpDown _attackInput = new();
    private readonly NumericUpDown _releaseInput = new();
    private readonly NumericUpDown _leaseInput = new();
    private readonly DataGridView _grid = new();
    private readonly Button _applyGainSelectedButton = new();
    private readonly Button _applyGainAllButton = new();
    private readonly Button _openStatusFileButton = new();
    private readonly Label _statusPathValue = new();

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
        panel.Controls.Add(new Label { Text = "Blocked:", AutoSize = true }, 4, 1);
        panel.Controls.Add(_blockedValue, 5, 1);

        panel.Controls.Add(new Label { Text = "Sensitivity:", AutoSize = true }, 0, 2);
        panel.Controls.Add(_sensitivitySlider, 1, 2);
        panel.Controls.Add(_thresholdValue, 2, 2);

        _sensitivitySlider.Minimum = 1;
        _sensitivitySlider.Maximum = 200;
        _sensitivitySlider.TickFrequency = 20;
        _sensitivitySlider.AutoSize = false;
        _sensitivitySlider.Height = 24;
        _sensitivitySlider.ValueChanged += OnSensitivityChanged;

        var calibrateButton = new Button { Text = "Calibrate Mic", AutoSize = true };
        calibrateButton.Click += async (_, _) => await CalibrateMicAsync();
        panel.Controls.Add(calibrateButton, 3, 2);

        panel.Controls.Add(new Label { Text = "Gate Level:", AutoSize = true }, 4, 2);
        panel.Controls.Add(new Label { Text = "5%", AutoSize = true }, 5, 2);

        panel.Controls.Add(new Label { Text = "Port:", AutoSize = true }, 0, 3);
        panel.Controls.Add(_portValue, 1, 3);
        panel.Controls.Add(new Label { Text = "Leader:", AutoSize = true }, 2, 3);
        panel.Controls.Add(_leaderValue, 3, 3);
        panel.Controls.Add(new Label { Text = "Co-Captain:", AutoSize = true }, 4, 3);
        panel.Controls.Add(_coValue, 5, 3);

        panel.Controls.Add(new Label { Text = "Attack (ms):", AutoSize = true }, 0, 4);
        panel.Controls.Add(_attackInput, 1, 4);
        panel.Controls.Add(new Label { Text = "Release (ms):", AutoSize = true }, 2, 4);
        panel.Controls.Add(_releaseInput, 3, 4);
        panel.Controls.Add(new Label { Text = "Lease (ms):", AutoSize = true }, 4, 4);
        panel.Controls.Add(_leaseInput, 5, 4);

        panel.Controls.Add(new Label { Text = "Peers:", AutoSize = true }, 0, 5);
        panel.Controls.Add(_peerCountValue, 1, 5);
        panel.Controls.Add(new Label { Text = "Last Peer:", AutoSize = true }, 2, 5);
        panel.Controls.Add(_lastPeerValue, 3, 5);

        ConfigureNumeric(_attackInput, 10, 1000, 50, OnTimingChanged);
        ConfigureNumeric(_releaseInput, 50, 2000, 300, OnTimingChanged);
        ConfigureNumeric(_leaseInput, 100, 2000, 600, OnTimingChanged);

        var gainPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        _gainSlider.Minimum = 30;
        _gainSlider.Maximum = 200;
        _gainSlider.TickFrequency = 10;
        _gainSlider.AutoSize = false;
        _gainSlider.Width = 160;
        _gainSlider.Height = 24;
        _gainSlider.ValueChanged += OnGainChanged;

        _autoGainCheck.Text = "Auto";
        _autoGainCheck.AutoSize = true;
        _autoGainCheck.CheckedChanged += OnAutoGainChanged;

        _applyGainSelectedButton.Text = "Apply to Selected";
        _applyGainSelectedButton.AutoSize = true;
        _applyGainSelectedButton.Click += (_, _) => ApplyGainToSelected();

        _applyGainAllButton.Text = "Apply to All";
        _applyGainAllButton.AutoSize = true;
        _applyGainAllButton.Click += (_, _) => ApplyGainToAll();

        gainPanel.Controls.Add(new Label { Text = "Gain:", AutoSize = true });
        gainPanel.Controls.Add(_gainSlider);
        gainPanel.Controls.Add(_gainValue);
        gainPanel.Controls.Add(_autoGainCheck);
        gainPanel.Controls.Add(_applyGainSelectedButton);
        gainPanel.Controls.Add(_applyGainAllButton);

        panel.Controls.Add(gainPanel, 0, 6);
        panel.SetColumnSpan(gainPanel, 6);

        var statusPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        _openStatusFileButton.Text = "Open Status JSON";
        _openStatusFileButton.AutoSize = true;
        _openStatusFileButton.Click += (_, _) => OpenStatusFile();
        _statusPathValue.AutoSize = true;
        statusPanel.Controls.Add(_openStatusFileButton);
        statusPanel.Controls.Add(_statusPathValue);

        panel.Controls.Add(statusPanel, 0, 7);
        panel.SetColumnSpan(statusPanel, 6);

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
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
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
        _blockedValue.Text = string.IsNullOrWhiteSpace(snapshot.BlockedReason) ? "-" : snapshot.BlockedReason;
        _leaderValue.Text = string.IsNullOrWhiteSpace(snapshot.LeaderId) ? "-" : snapshot.LeaderId;
        _coValue.Text = string.IsNullOrWhiteSpace(snapshot.CoCaptainId) ? "-" : snapshot.CoCaptainId;
        _peerCountValue.Text = snapshot.PeerCount.ToString();
        _lastPeerValue.Text = snapshot.LastPeerSeenSeconds.HasValue ? $"{snapshot.LastPeerSeenSeconds.Value:0}s" : "-";

        UpdateSensitivityControls(snapshot);
        UpdateGainControls(snapshot);
        UpdateGainButtons(snapshot);

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
        _portValue.Text = settings.GatePort.ToString();
        _statusPathValue.Text = $"({StatusWriterStatusPath()})";
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

    private void UpdateGainControls(GateSnapshot snapshot)
    {
        var settings = _engine.GetSettingsCopy();
        _suppressUpdates = true;
        try
        {
            var sliderValue = Math.Clamp((int)Math.Round(settings.GainScalar * 100), _gainSlider.Minimum, _gainSlider.Maximum);
            _gainSlider.Value = sliderValue;
            _gainValue.Text = settings.GainScalar.ToString("0.00");
            _autoGainCheck.Checked = settings.AutoGainEnabled;
        }
        finally
        {
            _suppressUpdates = false;
        }
    }

    private void UpdateGainButtons(GateSnapshot snapshot)
    {
        var leader = snapshot.EffectiveRole == Role.Leader;
        _applyGainSelectedButton.Enabled = leader;
        _applyGainAllButton.Enabled = leader;
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

    private void OnGainChanged(object? sender, EventArgs e)
    {
        if (_suppressUpdates)
        {
            return;
        }

        var scalar = _gainSlider.Value / 100.0;
        _gainValue.Text = scalar.ToString("0.00");
        _engine.UpdateSettings(s => s.GainScalar = scalar);
    }

    private void OnAutoGainChanged(object? sender, EventArgs e)
    {
        if (_suppressUpdates)
        {
            return;
        }

        _engine.UpdateSettings(s => s.AutoGainEnabled = _autoGainCheck.Checked);
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

    private void OpenStatusFile()
    {
        var path = StatusWriterStatusPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to open status file:{Environment.NewLine}{ex}",
                "DadBoard Gate", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string StatusWriterStatusPath()
    {
        return System.IO.Path.Combine(DadBoard.App.DataPaths.ResolveBaseDir(), "Gate", "status.json");
    }

    private void ApplyGainToSelected()
    {
        var targetId = GetSelectedPeerId();
        if (string.IsNullOrWhiteSpace(targetId))
        {
            MessageBox.Show(this, "Select a target in the list first.", "DadBoard Gate", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var settings = _engine.GetSettingsCopy();
        _engine.SendGainUpdate(targetId, settings.GainScalar, settings.AutoGainEnabled);
    }

    private void ApplyGainToAll()
    {
        var settings = _engine.GetSettingsCopy();
        _engine.SendGainUpdate(null, settings.GainScalar, settings.AutoGainEnabled);
    }

    private string? GetSelectedPeerId()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            return null;
        }

        return _grid.SelectedRows[0].Cells[0].Value?.ToString();
    }

    private async System.Threading.Tasks.Task CalibrateMicAsync()
    {
        using var form = new MeterForm("Calibrate Mic", "Say 1-5 for 3 seconds.");
        form.Show(this);
        try
        {
            await _engine.CalibrateMicAsync(form, form.CancellationToken);
        }
        finally
        {
            form.Close();
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
