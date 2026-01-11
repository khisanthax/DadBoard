<#
DadBoard Wipe-All Script

Usage:
  Dry run:
    .\DadBoard-WipeAll.ps1 -WhatIf -Verbose
  Real wipe:
    .\DadBoard-WipeAll.ps1 -Force -Verbose
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Force
)

$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Get-Location }
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$logPath = Join-Path $scriptRoot "DadBoard-WipeAll_$timestamp.log"

function Write-Log {
    param(
        [string]$Message,
        [string]$Level = "INFO"
    )
    $line = "{0} [{1}] {2}" -f (Get-Date).ToString("s"), $Level, $Message
    $line | Out-File -FilePath $logPath -Append -Encoding UTF8
    Write-Host $line
}

Write-Log "Starting DadBoard wipe-all. Log file: $logPath"

if (-not $Force) {
    $answer = Read-Host "This will remove DadBoard and all local state. Continue? (Y/N)"
    if ($answer -notin @("Y", "y")) {
        Write-Log "User canceled." "WARN"
        Write-Host "Canceled."
        return
    }
}

Write-Log "Stopping DadBoard processes..."
try {
    Get-Process -Name "DadBoard*" -ErrorAction SilentlyContinue | ForEach-Object {
        if ($PSCmdlet.ShouldProcess($_.ProcessName, "Stop-Process")) {
            try {
                Stop-Process -Id $_.Id -Force -ErrorAction Continue
                Write-Log "Stopped process $($_.ProcessName) (PID $($_.Id))."
            } catch {
                Write-Log "Failed to stop process $($_.ProcessName) (PID $($_.Id)): $($_.Exception.Message)" "WARN"
            }
        }
    }
} catch {
    Write-Log "Failed to enumerate DadBoard processes: $($_.Exception.Message)" "WARN"
}

$installDir = Join-Path $env:LOCALAPPDATA "Programs\DadBoard"
$localState = Join-Path $env:LOCALAPPDATA "DadBoard"
$programData = "C:\ProgramData\DadBoard"

$pathsToRemove = @($installDir, $localState, $programData)
foreach ($path in $pathsToRemove) {
    if (Test-Path $path) {
        if ($PSCmdlet.ShouldProcess($path, "Remove-Item -Recurse -Force")) {
            try {
                Remove-Item -Path $path -Recurse -Force -ErrorAction Continue
                Write-Log "Removed $path"
            } catch {
                Write-Log "Failed to remove ${path}: $($_.Exception.Message)" "WARN"
            }
        }
    } else {
        Write-Log "Not found: $path"
    }
}

$shortcuts = @(
    Join-Path $env:USERPROFILE "Desktop\DadBoard.lnk",
    "C:\Users\Public\Desktop\DadBoard.lnk"
)

foreach ($shortcut in $shortcuts) {
    if (Test-Path $shortcut) {
        if ($PSCmdlet.ShouldProcess($shortcut, "Remove-Item")) {
            try {
                Remove-Item -Path $shortcut -Force -ErrorAction Continue
                Write-Log "Removed shortcut $shortcut"
            } catch {
                Write-Log "Failed to remove shortcut ${shortcut}: $($_.Exception.Message)" "WARN"
            }
        }
    } else {
        Write-Log "Shortcut not found: $shortcut"
    }
}

$startMenuDirs = @(
    Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\DadBoard",
    "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\DadBoard"
)

foreach ($dir in $startMenuDirs) {
    if (Test-Path $dir) {
        if ($PSCmdlet.ShouldProcess($dir, "Remove-Item -Recurse -Force")) {
            try {
                Remove-Item -Path $dir -Recurse -Force -ErrorAction Continue
                Write-Log "Removed Start Menu folder $dir"
            } catch {
                Write-Log "Failed to remove Start Menu folder ${dir}: $($_.Exception.Message)" "WARN"
            }
        }
    } else {
        Write-Log "Start Menu folder not found: $dir"
    }
}

Write-Log "Removing scheduled tasks starting with DadBoard..."
try {
    $tasks = Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like "DadBoard*" }
    foreach ($task in $tasks) {
        if ($PSCmdlet.ShouldProcess($task.TaskName, "Unregister-ScheduledTask")) {
            try {
                Unregister-ScheduledTask -TaskName $task.TaskName -Confirm:$false -ErrorAction Continue
                Write-Log "Removed scheduled task $($task.TaskName)"
            } catch {
                Write-Log "Failed to remove scheduled task $($task.TaskName): $($_.Exception.Message)" "WARN"
            }
        }
    }
} catch {
    Write-Log "Failed to enumerate scheduled tasks: $($_.Exception.Message)" "WARN"
}

Write-Log "Removing services starting with DadBoard..."
try {
    $services = Get-Service -Name "DadBoard*" -ErrorAction SilentlyContinue
    foreach ($svc in $services) {
        if ($PSCmdlet.ShouldProcess($svc.Name, "Remove service")) {
            try {
                if ($svc.Status -ne "Stopped") {
                    Stop-Service -Name $svc.Name -Force -ErrorAction Continue
                }
                & sc.exe delete $svc.Name | Out-Null
                Write-Log "Removed service $($svc.Name)"
            } catch {
                Write-Log "Failed to remove service $($svc.Name): $($_.Exception.Message)" "WARN"
            }
        }
    }
} catch {
    Write-Log "Failed to enumerate services: $($_.Exception.Message)" "WARN"
}

Write-Log "Registry cleanup skipped (no known DadBoard-owned keys)."

Write-Log "Done. Next step: download Nightly DadBoardSetup.exe and install."
Write-Host "Done. Next step: download Nightly DadBoardSetup.exe and install."
