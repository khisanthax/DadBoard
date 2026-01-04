param(
    [int]$InviteTimeoutSec = 10,
    [string]$StatusPath = "$env:ProgramData\DadBoard\status.json"
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. "$scriptRoot\WriteStatus.ps1"
. "$scriptRoot\DetectProcesses.ps1"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$logDir = "$env:ProgramData\DadBoard\logs"
if (-not (Test-Path -Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$logPath = Join-Path $logDir 'AcceptInvite.log'

function Write-Log {
    param([string]$Message)
    $ts = (Get-Date).ToString('o')
    Add-Content -Path $logPath -Value "$ts $Message"
}

Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class Win32 {
    [DllImport(\"user32.dll\")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport(\"user32.dll\")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport(\"user32.dll\")] public static extern bool IsIconic(IntPtr hWnd);
}
"@

function Get-SteamWindow {
    $steam = Get-Process -Name 'steam' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $steam) { return $null }

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $steam.Id
    )
    $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    if ($windows.Count -eq 0) { return $null }

    foreach ($w in $windows) {
        if ($w.Current.Name -match 'Steam') { return $w }
    }

    return $windows[0]
}

function Invoke-LastInviteButton {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [int]$TimeoutSec
    )

    $start = Get-Date
    $foundAny = $false

    $namePlay = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        'Play Game'
    )
    $nameJoin = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        'Join Game'
    )
    $nameCond = New-Object System.Windows.Automation.OrCondition($namePlay, $nameJoin)
    $buttonCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button
    )
    $cond = New-Object System.Windows.Automation.AndCondition($buttonCond, $nameCond)

    while ((Get-Date) - $start -lt [TimeSpan]::FromSeconds($TimeoutSec)) {
        $buttons = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($buttons.Count -gt 0) {
            $foundAny = $true
            $target = $null
            for ($i = $buttons.Count - 1; $i -ge 0; $i--) {
                if (-not $buttons[$i].Current.IsOffscreen) {
                    $target = $buttons[$i]
                    break
                }
            }
            if (-not $target) { $target = $buttons[$buttons.Count - 1] }

            try {
                $pattern = $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                $pattern.Invoke()
                return @{ clicked = $true; foundAny = $foundAny }
            } catch {
                return @{ clicked = $false; foundAny = $foundAny; error = $_.Exception.Message }
            }
        }

        Start-Sleep -Milliseconds 500
    }

    return @{ clicked = $false; foundAny = $foundAny }
}

try {
    $status = Get-DadBoardStatus -Path $StatusPath
    $steamRunning = Test-AnyProcessRunning -Names @('steam')
    $status.steam.running = $steamRunning

    $gameProcesses = @()
    if ($status.game -and $status.game.processes) {
        $gameProcesses = @($status.game.processes)
    }
    $gameRunning = Test-AnyProcessRunning -Names $gameProcesses
    $status.game.running = $gameRunning

    if (-not $steamRunning) {
        $status.invite.lastAccept = (Get-Date).ToString('o')
        $status.invite.result = 'STEAM_NOT_RUNNING'
        Write-DadBoardStatus -Status $status -Path $StatusPath
        Write-Log 'Steam not running'
        exit 0
    }

    if (-not $gameRunning) {
        $status.invite.lastAccept = (Get-Date).ToString('o')
        $status.invite.result = 'GAME_NOT_RUNNING'
        Write-DadBoardStatus -Status $status -Path $StatusPath
        Write-Log 'Game not running'
        exit 0
    }

    $window = Get-SteamWindow
    if (-not $window) {
        $status.invite.lastAccept = (Get-Date).ToString('o')
        $status.invite.result = 'TIMEOUT'
        Write-DadBoardStatus -Status $status -Path $StatusPath
        Write-Log 'Steam window not found'
        exit 0
    }

    $handle = [IntPtr]$window.Current.NativeWindowHandle
    if ($handle -ne [IntPtr]::Zero) {
        if ([Win32]::IsIconic($handle)) { [Win32]::ShowWindow($handle, 9) | Out-Null }
        [Win32]::SetForegroundWindow($handle) | Out-Null
    }

    $result = Invoke-LastInviteButton -Window $window -TimeoutSec $InviteTimeoutSec
    $status.invite.lastAccept = (Get-Date).ToString('o')

    if ($result.clicked) {
        $status.invite.result = 'SUCCESS'
        Write-Log 'Invite accepted'
    } elseif ($result.foundAny) {
        $status.invite.result = 'TIMEOUT'
        $msg = if ($result.error) { $result.error } else { 'Found invite but failed to click' }
        Write-Log "Invite click failed: $msg"
    } else {
        $status.invite.result = 'NO_INVITE_FOUND'
        Write-Log 'No invite found'
    }

    Write-DadBoardStatus -Status $status -Path $StatusPath
} catch {
    $status = Get-DadBoardStatus -Path $StatusPath
    $status.invite.lastAccept = (Get-Date).ToString('o')
    $status.invite.result = 'ERROR'
    Write-DadBoardStatus -Status $status -Path $StatusPath

    Write-Log "ERROR: $($_.Exception.Message)"
    throw
}
