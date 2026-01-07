param(
    [string]$OutputDir = "$env:ProgramData\DadBoard\diag"
)

$baseDir = Join-Path $env:ProgramData "DadBoard"
$logDir = Join-Path $baseDir "logs"
$agentDir = Join-Path $baseDir "Agent"
$leaderDir = Join-Path $baseDir "Leader"

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$outPath = Join-Path $OutputDir "dadboard_diag_$timestamp.txt"

function Write-Line {
    param([string]$Text)
    $Text | Tee-Object -FilePath $outPath -Append
}

function Write-Section {
    param([string]$Title)
    Write-Line ""
    Write-Line "===== $Title ====="
}

Write-Section "Header"
Write-Line ("Timestamp: {0}" -f (Get-Date -Format "O"))
Write-Line ("Machine: {0}" -f $env:COMPUTERNAME)
Write-Line ("User: {0}" -f $env:USERNAME)

$appExe = Join-Path ${env:ProgramFiles} "DadBoard\\DadBoard.exe"
$agentExe = Join-Path $agentDir "DadBoard.Agent.exe"
$leaderExe = Join-Path $leaderDir "DadBoard.Leader.exe"
if (Test-Path -Path $appExe) {
    $ver = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($appExe).FileVersion
    Write-Line "DadBoard Version: $ver"
}
if (Test-Path -Path $agentExe) {
    $ver = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($agentExe).FileVersion
    Write-Line "Agent Version: $ver"
}
if (Test-Path -Path $leaderExe) {
    $ver = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($leaderExe).FileVersion
    Write-Line "Leader Version: $ver"
}

Write-Section "Environment"
try {
    $os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber
    Write-Line ("OS: {0} {1} (Build {2})" -f $os.Caption, $os.Version, $os.BuildNumber)
} catch { }
Write-Line (".NET: {0}" -f [System.Environment]::Version)
try {
    $tz = (Get-TimeZone).Id
    Write-Line "Timezone: $tz"
} catch { }
try {
    $ips = Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -ne "127.0.0.1" } | Select-Object -ExpandProperty IPAddress
    Write-Line ("IPs: {0}" -f ($ips -join ", "))
} catch { }

Write-Section "Process/Ports"
try {
    $procs = Get-Process | Where-Object { $_.ProcessName -like "DadBoard*" }
    if ($procs) {
        $procs | Select-Object ProcessName, Id, StartTime | Out-String | Tee-Object -FilePath $outPath -Append
    } else {
        Write-Line "No DadBoard processes found."
    }
} catch { }
try {
    $ports = Get-NetTCPConnection -State Listen | Where-Object { $_.LocalPort -in 39555,39601 }
    if ($ports) {
        $ports | Select-Object LocalAddress, LocalPort, OwningProcess | Out-String | Tee-Object -FilePath $outPath -Append
    }
} catch { }

Write-Section "Config Snapshot"
$agentConfigLocal = Join-Path $env:LOCALAPPDATA "DadBoard\\Agent\\agent.config.json"
$leaderConfig = Join-Path $leaderDir "leader.config.json"
if (Test-Path -Path $agentConfigLocal) {
    Write-Line "agent.config.json (LocalAppData):"
    Get-Content -Path $agentConfigLocal | Tee-Object -FilePath $outPath -Append
}
if (Test-Path -Path $leaderConfig) {
    Write-Line "leader.config.json:"
    Get-Content -Path $leaderConfig | Tee-Object -FilePath $outPath -Append
}

Write-Section "Discovery Snapshot"
$knownAgents = Join-Path $baseDir "known_agents.json"
$leaderState = Join-Path $leaderDir "leader_state.json"
$agentState = Join-Path $agentDir "agent_state.json"
if (Test-Path -Path $knownAgents) {
    Write-Line "known_agents.json:"
    Get-Content -Path $knownAgents | Tee-Object -FilePath $outPath -Append
}
if (Test-Path -Path $leaderState) {
    Write-Line "leader_state.json:"
    Get-Content -Path $leaderState | Tee-Object -FilePath $outPath -Append
}
if (Test-Path -Path $agentState) {
    Write-Line "agent_state.json:"
    Get-Content -Path $agentState | Tee-Object -FilePath $outPath -Append
}

Write-Section "Inventory Snapshot"
$leaderInventory = Join-Path $env:LOCALAPPDATA "DadBoard\\Leader\\leader_inventory.json"
$agentInventories = Join-Path $env:LOCALAPPDATA "DadBoard\\Leader\\agent_inventories.json"
if (Test-Path -Path $leaderInventory) {
    try {
        $leaderData = Get-Content -Path $leaderInventory -Raw | ConvertFrom-Json
        $leaderCount = if ($leaderData.games) { $leaderData.games.Count } else { 0 }
        Write-Line ("Leader catalog count: {0}" -f $leaderCount)
        Write-Line ("Leader catalog timestamp: {0}" -f $leaderData.ts)
    } catch {
        Write-Line ("Leader inventory parse failed: {0}" -f $_.Exception.Message)
    }
} else {
    Write-Line "Leader inventory cache not found."
}

if (Test-Path -Path $agentInventories) {
    try {
        $agentData = Get-Content -Path $agentInventories -Raw | ConvertFrom-Json
        if ($agentData.inventories) {
            foreach ($inv in $agentData.inventories) {
                $count = if ($inv.games) { $inv.games.Count } else { 0 }
                $sample = @()
                if ($inv.games) {
                    $sample = $inv.games | Select-Object -First 10 | ForEach-Object { $_.appId }
                }
                Write-Line ("Agent {0} ({1}) games: {2}" -f $inv.pcId, $inv.machineName, $count)
                if ($sample.Count -gt 0) {
                    Write-Line ("Agent {0} sample appIds: {1}" -f $inv.pcId, ($sample -join ", "))
                }
                if ($inv.error) {
                    Write-Line ("Agent {0} error: {1}" -f $inv.pcId, $inv.error)
                }
                if ($inv.ts) {
                    Write-Line ("Agent {0} last scan: {1}" -f $inv.pcId, $inv.ts)
                }
            }
        } else {
            Write-Line "Agent inventories cache empty."
        }
    } catch {
        Write-Line ("Agent inventories parse failed: {0}" -f $_.Exception.Message)
    }
} else {
    Write-Line "Agent inventories cache not found."
}

Write-Section "WebSocket Health"
if (Test-Path -Path $leaderState) {
    Write-Line "Leader WebSocket state is captured in leader_state.json."
}
if (Test-Path -Path $agentState) {
    Write-Line "Agent WebSocket state is captured in agent_state.json."
}

Write-Section "Logs (last 200 lines)"
$leaderLog = Join-Path $logDir "leader.log"
$agentLog = Join-Path $logDir "agent.log"
if (Test-Path -Path $leaderLog) {
    Write-Line "leader.log:"
    Get-Content -Path $leaderLog -Tail 200 | Tee-Object -FilePath $outPath -Append
}
if (Test-Path -Path $agentLog) {
    Write-Line "agent.log:"
    Get-Content -Path $agentLog -Tail 200 | Tee-Object -FilePath $outPath -Append
}

Write-Section "Firewall"
try {
    $rules = Get-NetFirewallRule -DisplayName "DadBoard*" -ErrorAction SilentlyContinue
    if ($rules) {
        $rules | Select-Object DisplayName, Enabled, Direction, Action | Out-String | Tee-Object -FilePath $outPath -Append
    } else {
        Write-Line "No DadBoard firewall rules found."
    }
} catch { }

Write-Section "Hints"
Write-Line "- If agents are OFFLINE, check UDP port 39555 and firewall rules."
Write-Line "- If WS connection fails, confirm agent wsPort 39601 is listening."
Write-Line "- If commands time out, check agent.log for launch errors."

Write-Line ""
Write-Line "Diagnostic bundle saved to: $outPath"
