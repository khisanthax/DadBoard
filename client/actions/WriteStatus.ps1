function New-DadBoardStatus {
    param([string]$Name)

    [pscustomobject]@{
        pcName = $Name
        lastUpdate = $null
        steam = [pscustomobject]@{
            running = $false
        }
        game = [pscustomobject]@{
            appid = $null
            name = $null
            processes = @()
            running = $false
            lastLaunch = $null
            launchResult = $null
        }
        invite = [pscustomobject]@{
            lastAccept = $null
            result = $null
        }
    }
}

function Get-DadBoardStatus {
    param([string]$Path)

    if (-not $Path) {
        $Path = Join-Path $env:ProgramData 'DadBoard\status.json'
    }

    if (Test-Path -Path $Path) {
        try {
            $raw = Get-Content -Path $Path -Raw -ErrorAction Stop
            $obj = $raw | ConvertFrom-Json -ErrorAction Stop
        } catch {
            $obj = $null
        }
    }

    if (-not $obj) {
        $obj = New-DadBoardStatus -Name $env:COMPUTERNAME
    }

    if (-not $obj.pcName) { $obj.pcName = $env:COMPUTERNAME }
    if (-not $obj.steam) { $obj | Add-Member -NotePropertyName steam -NotePropertyValue ([pscustomobject]@{ running = $false }) -Force }
    if (-not $obj.game) {
        $obj | Add-Member -NotePropertyName game -NotePropertyValue ([pscustomobject]@{ appid=$null; name=$null; processes=@(); running=$false; lastLaunch=$null; launchResult=$null }) -Force
    }
    if (-not $obj.invite) {
        $obj | Add-Member -NotePropertyName invite -NotePropertyValue ([pscustomobject]@{ lastAccept=$null; result=$null }) -Force
    }

    return $obj
}

function Write-DadBoardStatus {
    param(
        [Parameter(Mandatory=$true)]
        [psobject]$Status,
        [string]$Path
    )

    if (-not $Path) {
        $Path = Join-Path $env:ProgramData 'DadBoard\status.json'
    }

    $Status.lastUpdate = (Get-Date).ToString('o')
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    $tmp = "$Path.tmp"
    $json = $Status | ConvertTo-Json -Depth 8
    Set-Content -Path $tmp -Value $json -Encoding UTF8
    Move-Item -Path $tmp -Destination $Path -Force
}
