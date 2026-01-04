param()

function Normalize-ProcessName {
    param([string]$Name)
    if (-not $Name) { return $null }
    $base = [System.IO.Path]::GetFileNameWithoutExtension($Name)
    return $base
}

function Test-AnyProcessRunning {
    param([string[]]$Names)
    if (-not $Names -or $Names.Count -eq 0) { return $false }

    foreach ($name in $Names) {
        $n = Normalize-ProcessName -Name $name
        if (-not $n) { continue }
        if (Get-Process -Name $n -ErrorAction SilentlyContinue) {
            return $true
        }
    }

    return $false
}

function Get-RunningProcessNames {
    param([string[]]$Names)
    $running = @()
    if (-not $Names -or $Names.Count -eq 0) { return $running }

    foreach ($name in $Names) {
        $n = Normalize-ProcessName -Name $name
        if (-not $n) { continue }
        $procs = Get-Process -Name $n -ErrorAction SilentlyContinue
        if ($procs) { $running += $name }
    }

    return $running
}
