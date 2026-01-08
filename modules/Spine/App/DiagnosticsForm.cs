using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using DadBoard.Leader;
using DadBoard.Spine.Shared;

namespace DadBoard.App;

public sealed class DiagnosticsForm : Form
{
    private readonly TableLayoutPanel _layout = new();
    private readonly TextBox _runningPath = new();
    private readonly TextBox _expectedPath = new();
    private readonly TextBox _version = new();
    private readonly TextBox _updateSource = new();
    private readonly TextBox _logsPath = new();
    private readonly ListView _agentVersions = new();
    private readonly Label _devWarning = new();
    private readonly Button _launchInstalledButton = new();

    private LeaderService? _leader;

    public DiagnosticsForm(LeaderService? leader)
    {
        _leader = leader;
        Text = "DadBoard Diagnostics";
        Size = new Size(760, 520);
        StartPosition = FormStartPosition.CenterParent;

        _layout.Dock = DockStyle.Fill;
        _layout.ColumnCount = 2;
        _layout.RowCount = 8;
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _devWarning.AutoSize = true;
        _devWarning.ForeColor = Color.DarkRed;
        _devWarning.Font = new Font(Font, FontStyle.Bold);
        _devWarning.Visible = false;
        _layout.SetColumnSpan(_devWarning, 2);

        _launchInstalledButton.Text = "Launch Installed App";
        _launchInstalledButton.AutoSize = true;
        _launchInstalledButton.Visible = false;
        _launchInstalledButton.Click += (_, _) => LaunchInstalledAppAndExit();

        AddRow("Running exe", _runningPath, 1);
        AddRow("Expected install", _expectedPath, 2);
        AddRow("App version", _version, 3);
        AddRow("Update source", _updateSource, 4);
        AddRow("Logs folder", _logsPath, 5);

        var agentLabel = new Label
        {
            Text = "Agent versions",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        };
        _layout.Controls.Add(agentLabel, 0, 6);

        _agentVersions.View = View.Details;
        _agentVersions.FullRowSelect = true;
        _agentVersions.GridLines = true;
        _agentVersions.Columns.Add("PC", 200);
        _agentVersions.Columns.Add("Version", 120);
        _agentVersions.Dock = DockStyle.Fill;
        _layout.Controls.Add(_agentVersions, 1, 6);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true
        };
        var openLogsButton = new Button
        {
            Text = "Open logs folder",
            AutoSize = true
        };
        openLogsButton.Click += (_, _) => OpenLogsFolder();

        var copyButton = new Button
        {
            Text = "Copy diagnostics",
            AutoSize = true
        };
        copyButton.Click += (_, _) => CopyDiagnostics();

        buttonPanel.Controls.Add(openLogsButton);
        buttonPanel.Controls.Add(copyButton);
        buttonPanel.Controls.Add(_launchInstalledButton);

        _layout.Controls.Add(_devWarning, 0, 0);
        _layout.Controls.Add(buttonPanel, 0, 7);
        _layout.SetColumnSpan(buttonPanel, 2);

        Controls.Add(_layout);

        UpdateSnapshot(leader);
    }

    public void UpdateSnapshot(LeaderService? leader)
    {
        _leader = leader;
        var runningPath = Process.GetCurrentProcess().MainModule?.FileName ?? "Unknown";
        _runningPath.Text = runningPath;
        _expectedPath.Text = DadBoardPaths.InstalledExePath;
        _version.Text = VersionUtil.GetCurrentVersion();
        var updateConfig = UpdateConfigStore.Load();
        _updateSource.Text = string.IsNullOrWhiteSpace(updateConfig.ManifestUrl)
            ? "Not configured"
            : updateConfig.ManifestUrl;
        _logsPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DadBoard", "logs");

        UpdateDevWarning(runningPath);
        UpdateAgentVersions();
    }

    private void UpdateDevWarning(string runningPath)
    {
        var isDev = runningPath.IndexOf("\\bin\\Release\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    runningPath.IndexOf("\\publish\\", StringComparison.OrdinalIgnoreCase) >= 0;

        _devWarning.Visible = isDev;
        _devWarning.Text = isDev
            ? "Warning: running from a dev build output. Updates apply to the installed app."
            : "";

        var installedExists = File.Exists(DadBoardPaths.InstalledExePath);
        _launchInstalledButton.Visible = isDev && installedExists;
    }

    private void UpdateAgentVersions()
    {
        _agentVersions.Items.Clear();
        if (_leader == null)
        {
            _agentVersions.Items.Add(new ListViewItem(new[] { "Leader disabled", "-" }));
            return;
        }

        var agents = _leader.GetAgentsSnapshot()
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (agents.Count == 0)
        {
            _agentVersions.Items.Add(new ListViewItem(new[] { "No agents", "-" }));
            return;
        }

        foreach (var agent in agents)
        {
            _agentVersions.Items.Add(new ListViewItem(new[]
            {
                agent.Name,
                string.IsNullOrWhiteSpace(agent.Version) ? "-" : VersionUtil.Normalize(agent.Version)
            }));
        }
    }

    private void OpenLogsFolder()
    {
        var path = _logsPath.Text;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            MessageBox.Show("Logs folder not found.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void CopyDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Running exe: {_runningPath.Text}");
        sb.AppendLine($"Expected install: {_expectedPath.Text}");
        sb.AppendLine($"Version: {_version.Text}");
        sb.AppendLine($"Update source: {_updateSource.Text}");
        sb.AppendLine($"Logs folder: {_logsPath.Text}");

        if (_leader != null)
        {
            foreach (var agent in _leader.GetAgentsSnapshot())
            {
                sb.AppendLine($"Agent {agent.Name} ({agent.PcId}): {agent.Version}");
            }
        }
        else
        {
            sb.AppendLine("Leader disabled.");
        }

        Clipboard.SetText(sb.ToString());
    }

    private void LaunchInstalledAppAndExit()
    {
        if (!File.Exists(DadBoardPaths.InstalledExePath))
        {
            MessageBox.Show("Installed DadBoard.exe not found.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo(DadBoardPaths.InstalledExePath) { UseShellExecute = true });
        Application.Exit();
    }

    private void AddRow(string labelText, TextBox target, int row)
    {
        var label = new Label
        {
            Text = labelText,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        };

        target.ReadOnly = true;
        target.BorderStyle = BorderStyle.FixedSingle;
        target.Dock = DockStyle.Fill;

        _layout.Controls.Add(label, 0, row);
        _layout.Controls.Add(target, 1, row);
    }
}
