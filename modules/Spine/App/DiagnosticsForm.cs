using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
    private readonly TextBox _updaterAction = new();
    private readonly TextBox _updaterInstalled = new();
    private readonly TextBox _updaterAvailable = new();
    private readonly TextBox _updaterResult = new();
    private readonly TextBox _updaterMessage = new();
    private readonly TextBox _updaterManifest = new();
    private readonly TextBox _updaterLastRun = new();
    private readonly TextBox _updaterLogPath = new();
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
        _layout.RowCount = 16;
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        for (var i = 0; i < 14; i++)
        {
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
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
        AddRow("Updater action", _updaterAction, 4);
        AddRow("Updater installed", _updaterInstalled, 5);
        AddRow("Updater available", _updaterAvailable, 6);
        AddRow("Updater result", _updaterResult, 7);
        AddRow("Updater message", _updaterMessage, 8);
        AddRow("Updater manifest", _updaterManifest, 9);
        AddRow("Updater last run", _updaterLastRun, 10);
        AddRow("Updater log", _updaterLogPath, 11);
        AddRow("Logs folder", _logsPath, 12);

        var agentLabel = new Label
        {
            Text = "Agent versions",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        };
        _layout.Controls.Add(agentLabel, 0, 13);

        _agentVersions.View = View.Details;
        _agentVersions.FullRowSelect = true;
        _agentVersions.GridLines = true;
        _agentVersions.Columns.Add("PC", 200);
        _agentVersions.Columns.Add("Version", 120);
        _agentVersions.Dock = DockStyle.Fill;
        _layout.Controls.Add(_agentVersions, 1, 13);

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
        _layout.Controls.Add(buttonPanel, 0, 15);
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
        _logsPath.Text = UpdaterStatusStore.LogDir;

        _updaterAction.Text = "Loading...";
        _updaterInstalled.Text = "Loading...";
        _updaterAvailable.Text = "Loading...";
        _updaterResult.Text = "Loading...";
        _updaterMessage.Text = "Loading...";
        _updaterManifest.Text = "Loading...";
        _updaterLastRun.Text = "Loading...";
        _updaterLogPath.Text = "Loading...";

        UpdateDevWarning(runningPath);
        UpdateAgentVersions();
        LoadUpdaterStatusAsync();
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

    private void LoadUpdaterStatusAsync()
    {
        _ = Task.Run(() =>
        {
            return UpdaterStatusStore.Load();
        }).ContinueWith(task =>
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            var status = task.IsFaulted ? null : task.Result;
            if (status == null)
            {
                _updaterAction.Text = "-";
                _updaterInstalled.Text = "-";
                _updaterAvailable.Text = "-";
                _updaterResult.Text = "No updater status yet.";
                _updaterMessage.Text = "-";
                _updaterManifest.Text = "-";
                _updaterLastRun.Text = "-";
                _updaterLogPath.Text = Path.Combine(UpdaterStatusStore.LogDir, "updater.log");
                return;
            }

            _updaterAction.Text = string.IsNullOrWhiteSpace(status.Action) ? "-" : status.Action;
            _updaterInstalled.Text = string.IsNullOrWhiteSpace(status.InstalledVersion) ? "-" : status.InstalledVersion;
            _updaterAvailable.Text = string.IsNullOrWhiteSpace(status.AvailableVersion) ? "-" : status.AvailableVersion;
            _updaterResult.Text = string.IsNullOrWhiteSpace(status.Result) ? "-" : status.Result;
            _updaterMessage.Text = string.IsNullOrWhiteSpace(status.Message) ? "-" : status.Message;
            _updaterManifest.Text = string.IsNullOrWhiteSpace(status.ManifestUrl) ? "-" : status.ManifestUrl;
            _updaterLastRun.Text = string.IsNullOrWhiteSpace(status.TimestampUtc) ? "-" : status.TimestampUtc;
            _updaterLogPath.Text = string.IsNullOrWhiteSpace(status.LogPath) ? "-" : status.LogPath;
        }, TaskScheduler.FromCurrentSynchronizationContext());
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
        sb.AppendLine($"Updater action: {_updaterAction.Text}");
        sb.AppendLine($"Updater installed: {_updaterInstalled.Text}");
        sb.AppendLine($"Updater available: {_updaterAvailable.Text}");
        sb.AppendLine($"Updater result: {_updaterResult.Text}");
        sb.AppendLine($"Updater message: {_updaterMessage.Text}");
        sb.AppendLine($"Updater manifest: {_updaterManifest.Text}");
        sb.AppendLine($"Updater last run: {_updaterLastRun.Text}");
        sb.AppendLine($"Updater log: {_updaterLogPath.Text}");
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

    private void AddRow(string labelText, Control target, int row)
    {
        var label = new Label
        {
            Text = labelText,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        };

        if (target is TextBox textBox)
        {
            textBox.ReadOnly = true;
            textBox.BorderStyle = BorderStyle.FixedSingle;
            textBox.Dock = DockStyle.Fill;
        }
        else
        {
            target.Dock = DockStyle.Fill;
        }

        _layout.Controls.Add(label, 0, row);
        _layout.Controls.Add(target, 1, row);
    }
}
