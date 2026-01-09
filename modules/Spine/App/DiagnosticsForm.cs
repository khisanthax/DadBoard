using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
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
    private readonly TextBox _updateSource = new();
    private readonly TextBox _logsPath = new();
    private readonly TextBox _updateError = new();
    private readonly TextBox _mirrorStatus = new();
    private readonly TextBox _mirrorHostUrl = new();
    private readonly TextBox _mirrorLastManifest = new();
    private readonly TextBox _mirrorLastDownload = new();
    private readonly TextBox _mirrorCached = new();
    private readonly TextBox _manifestUrlInput = new();
    private readonly TextBox _localHostIpInput = new();
    private readonly Button _saveManifestButton = new();
    private readonly Button _selfTestButton = new();
    private readonly Label _selfTestSummary = new();
    private readonly TextBox _selfTestLog = new();
    private readonly ListView _agentVersions = new();
    private readonly Label _devWarning = new();
    private readonly Button _launchInstalledButton = new();

    private LeaderService? _leader;
    private int _selfTestRunning;
    private readonly System.Net.Http.HttpClient _selfTestHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

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
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
        AddRow("Update source error", _updateError, 5);
        AddRow("Set manifest URL", BuildManifestEditor(), 6);
        AddRow("Mirror status", _mirrorStatus, 7);
        AddRow("Mirror host URL", _mirrorHostUrl, 8);
        AddRow("Last manifest fetch", _mirrorLastManifest, 9);
        AddRow("Last zip download", _mirrorLastDownload, 10);
        AddRow("Cached versions", _mirrorCached, 11);
        AddRow("Logs folder", _logsPath, 12);
        AddRow("Update self-test", BuildSelfTestPanel(), 13);

        var agentLabel = new Label
        {
            Text = "Agent versions",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        };
        _layout.Controls.Add(agentLabel, 0, 14);

        _agentVersions.View = View.Details;
        _agentVersions.FullRowSelect = true;
        _agentVersions.GridLines = true;
        _agentVersions.Columns.Add("PC", 200);
        _agentVersions.Columns.Add("Version", 120);
        _agentVersions.Dock = DockStyle.Fill;
        _layout.Controls.Add(_agentVersions, 1, 14);

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
        _updateSource.Text = "Loading...";
        _updateError.Text = "";
        _logsPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DadBoard", "logs");

        UpdateDevWarning(runningPath);
        UpdateAgentVersions();
        LoadUpdateDetailsAsync();
        UpdateMirrorDetails();
    }

    private Control BuildManifestEditor()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true
        };

        var manifestLabel = new Label
        {
            Text = "Manifest URL",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft
        };

        var hostLabel = new Label
        {
            Text = "Local host IP",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _manifestUrlInput.Width = 320;
        _localHostIpInput.Width = 140;
        _saveManifestButton.Text = "Save";
        _saveManifestButton.AutoSize = true;
        _saveManifestButton.Click += (_, _) => SaveManifestUrl();

        panel.Controls.Add(manifestLabel);
        panel.Controls.Add(_manifestUrlInput);
        panel.Controls.Add(hostLabel);
        panel.Controls.Add(_localHostIpInput);
        panel.Controls.Add(_saveManifestButton);
        return panel;
    }

    private Control BuildSelfTestPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight
        };

        _selfTestButton.Text = "Self-test Update Pipeline";
        _selfTestButton.AutoSize = true;
        _selfTestButton.Click += async (_, _) => await RunSelfTestAsync();

        _selfTestSummary.Text = "Not run";
        _selfTestSummary.AutoSize = true;

        header.Controls.Add(_selfTestButton);
        header.Controls.Add(_selfTestSummary);

        _selfTestLog.Multiline = true;
        _selfTestLog.ReadOnly = true;
        _selfTestLog.ScrollBars = ScrollBars.Vertical;
        _selfTestLog.Height = 100;
        _selfTestLog.Dock = DockStyle.Fill;

        panel.Controls.Add(header, 0, 0);
        panel.Controls.Add(_selfTestLog, 0, 1);
        return panel;
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

    private void UpdateMirrorDetails()
    {
        if (_leader == null)
        {
            _mirrorStatus.Text = "Leader disabled";
            _mirrorHostUrl.Text = "";
            _mirrorLastManifest.Text = "";
            _mirrorLastDownload.Text = "";
            _mirrorCached.Text = "";
            return;
        }

        var snapshot = _leader.GetUpdateMirrorSnapshot();
        _mirrorStatus.Text = snapshot.Enabled ? "Enabled" : "Disabled";
        _mirrorHostUrl.Text = snapshot.LocalHostUrl;
        _mirrorLastManifest.Text = string.IsNullOrWhiteSpace(snapshot.LastManifestFetchUtc)
            ? snapshot.LastManifestResult
            : $"{snapshot.LastManifestFetchUtc} ({snapshot.LastManifestResult})";
        _mirrorLastDownload.Text = string.IsNullOrWhiteSpace(snapshot.LastDownloadUtc)
            ? snapshot.LastDownloadResult
            : $"{snapshot.LastDownloadUtc} ({snapshot.LastDownloadResult})";
        _mirrorCached.Text = snapshot.CachedVersions;

        if (string.IsNullOrWhiteSpace(_updateError.Text) && !string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            _updateError.Text = snapshot.LastError;
        }
    }

    private void LoadUpdateDetailsAsync()
    {
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            UpdateConfig? config = null;
            UpdateState? state = null;
            string? error = null;

            try
            {
                config = UpdateConfigStore.Load();
            }
            catch (Exception ex)
            {
                error = $"Config load failed: {ex.Message}";
            }

            try
            {
                state = UpdateStateStore.Load();
            }
            catch (Exception ex)
            {
                error = error ?? $"State load failed: {ex.Message}";
            }

            return (config, state, error);
        }).ContinueWith(task =>
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            if (task.IsFaulted)
            {
                _updateSource.Text = "Not configured";
                _updateError.Text = task.Exception?.GetBaseException().Message ?? "Update info failed.";
                return;
            }

            var (config, state, error) = task.Result;
            var source = config?.ManifestUrl ?? "";
            _manifestUrlInput.Text = source;
            _localHostIpInput.Text = config?.LocalHostIp ?? "";
            _updateSource.Text = string.IsNullOrWhiteSpace(source) ? "Not configured" : source;

            if (!string.IsNullOrWhiteSpace(error))
            {
                _updateError.Text = error;
                return;
            }

            if (state != null)
            {
                if (state.UpdatesDisabled)
                {
                    _updateError.Text = "Updates disabled due to repeated failures.";
                }
                else if (!string.IsNullOrWhiteSpace(state.LastError))
                {
                    _updateError.Text = state.LastError;
                }
                else
                {
                    _updateError.Text = "None";
                }
            }
            else
            {
                _updateError.Text = "None";
            }
        }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
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
        sb.AppendLine($"Update source error: {_updateError.Text}");
        sb.AppendLine($"Mirror status: {_mirrorStatus.Text}");
        sb.AppendLine($"Mirror host URL: {_mirrorHostUrl.Text}");
        sb.AppendLine($"Last manifest fetch: {_mirrorLastManifest.Text}");
        sb.AppendLine($"Last zip download: {_mirrorLastDownload.Text}");
        sb.AppendLine($"Cached versions: {_mirrorCached.Text}");
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

    private void SaveManifestUrl()
    {
        var url = _manifestUrlInput.Text.Trim();
        var ipOverride = _localHostIpInput.Text.Trim();
        var config = UpdateConfigStore.Load();
        config.ManifestUrl = url;
        config.LocalHostIp = ipOverride;
        config.Source = string.IsNullOrWhiteSpace(url) ? "" : "github_mirror";
        config.MirrorEnabled = !string.IsNullOrWhiteSpace(url);
        UpdateConfigStore.Save(config);
        _leader?.ReloadUpdateConfig();
        _updateSource.Text = string.IsNullOrWhiteSpace(url) ? "Not configured" : url;
        _updateError.Text = "None";
        UpdateMirrorDetails();
    }

    private async Task RunSelfTestAsync()
    {
        if (_leader == null)
        {
            MessageBox.Show("Enable Leader to run the update self-test.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (Interlocked.Exchange(ref _selfTestRunning, 1) == 1)
        {
            return;
        }

        _selfTestButton.Enabled = false;
        _selfTestSummary.Text = "Running...";
        _selfTestLog.Clear();

        var passed = true;

        try
        {
            AppendSelfTest("Step A: Loading update.config.json...");
            var config = await Task.Run(UpdateConfigStore.Load).ConfigureAwait(true);
            var (hostUrl, reason) = _leader.GetLocalUpdateHostUrlWithReason();
            AppendSelfTest($"Config source={config.Source} manifest_url={config.ManifestUrl}");
            AppendSelfTest($"mirror_enabled={config.MirrorEnabled} poll_minutes={config.MirrorPollMinutes} local_host={hostUrl} ({reason})");

            AppendSelfTest("Step B: Fetching GitHub manifest...");
            if (string.IsNullOrWhiteSpace(config.ManifestUrl))
            {
                passed = false;
                AppendSelfTest("FAIL: manifest_url not configured.");
            }
            else
            {
                var manifest = await FetchManifestAsync(config.ManifestUrl).ConfigureAwait(true);
                if (manifest == null)
                {
                    passed = false;
                    AppendSelfTest("FAIL: Unable to fetch/parse manifest.");
                }
                else
                {
                    AppendSelfTest($"OK: latest_version={manifest.LatestVersion} package_url={manifest.PackageUrl}");
                }
            }

            AppendSelfTest("Step C: Checking mirror cache...");
            var cacheDir = Path.Combine(DadBoardPaths.UpdateSourceDir, "cache");
            if (!Directory.Exists(cacheDir))
            {
                AppendSelfTest($"Cache folder missing: {cacheDir}");
            }
            else
            {
                var files = Directory.GetFiles(cacheDir, "DadBoard-*.zip");
                if (files.Length == 0)
                {
                    AppendSelfTest("Cache zips=0 newest=none");
                }
                else
                {
                    var newest = files.OrderByDescending(File.GetLastWriteTimeUtc).First();
                    var newestTime = File.GetLastWriteTimeUtc(newest).ToString("O");
                    var names = string.Join(", ", files.Select(Path.GetFileName));
                    AppendSelfTest($"Cache zips={files.Length} newest={Path.GetFileName(newest)} mtime={newestTime}");
                    AppendSelfTest($"Cache files: {names}");
                }
            }

            AppendSelfTest("Step D: Fetching leader-hosted manifest...");
            var leaderManifestUrl = $"{hostUrl}/updates/latest.json";
            var localManifest = await FetchManifestAsync(leaderManifestUrl).ConfigureAwait(true);
            if (localManifest == null)
            {
                passed = false;
                AppendSelfTest("FAIL: Unable to fetch leader manifest.");
            }
            else
            {
                var uri = new Uri(localManifest.PackageUrl);
                AppendSelfTest($"OK: leader package_url host={uri.Host} port={uri.Port}");
            }

            if (localManifest != null)
            {
                AppendSelfTest("Step E: Checking leader-hosted zip...");
                var ok = await CheckZipAsync(localManifest.PackageUrl).ConfigureAwait(true);
                if (!ok)
                {
                    passed = false;
                    AppendSelfTest("FAIL: leader zip check failed.");
                }
                else
                {
                    AppendSelfTest("OK: leader zip accessible.");
                }
            }
        }
        catch (Exception ex)
        {
            passed = false;
            AppendSelfTest($"FAIL: {ex.Message}");
        }
        finally
        {
            _selfTestSummary.Text = passed ? "PASS" : "FAIL";
            _selfTestButton.Enabled = true;
            Interlocked.Exchange(ref _selfTestRunning, 0);
        }
    }

    private async Task<UpdateManifest?> FetchManifestAsync(string url)
    {
        try
        {
            var json = await _selfTestHttp.GetStringAsync(url).ConfigureAwait(true);
            return JsonSerializer.Deserialize<UpdateManifest>(json, JsonUtil.Options);
        }
        catch (Exception ex)
        {
            AppendSelfTest($"Manifest fetch error: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> CheckZipAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await _selfTestHttp.SendAsync(request).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                AppendSelfTest($"Zip check HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                return false;
            }

            var length = response.Content.Headers.ContentLength ?? 0;
            if (length <= 0)
            {
                AppendSelfTest("Zip check content-length missing/zero.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AppendSelfTest($"Zip check error: {ex.Message}");
            return false;
        }
    }

    private void AppendSelfTest(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(AppendSelfTest), message);
            return;
        }

        _selfTestLog.AppendText($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
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
