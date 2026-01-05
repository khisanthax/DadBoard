[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [ValidateSet("quiet","normal","mute")]
  [string]$Preset,
  [pscredential]$Credential
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$configPath = Join-Path $scriptRoot "config.json"

if (-not (Test-Path -Path $configPath)) {
  Write-Error "Missing config: $configPath"
  exit 1
}

$config = Get-Content -Path $configPath -Raw | ConvertFrom-Json
$computers = @($config.computers | Where-Object { $_ -and $_ -ne "" })
$tasks = $config.tasks

if (-not $computers -or $computers.Count -eq 0) {
  Write-Error "No computers defined in config.json."
  exit 1
}

$taskName = $tasks.$Preset
if (-not $taskName) {
  Write-Error "Missing tasks.$Preset in config.json."
  exit 1
}

function Invoke-RemoteTask {
  param(
    [string]$Computer,
    [string]$TaskName,
    [pscredential]$Credential
  )

  $args = @("/Run", "/S", $Computer, "/TN", $TaskName)
  if ($Credential) {
    $args += @("/U", $Credential.UserName, "/P", $Credential.GetNetworkCredential().Password)
  }

  $output = & schtasks.exe @args 2>&1
  $exitCode = $LASTEXITCODE

  [pscustomobject]@{
    Computer = $Computer
    Task = $TaskName
    Success = ($exitCode -eq 0)
    ExitCode = $exitCode
    Output = ($output -join "`n")
  }
}

$results = @()
foreach ($pc in $computers) {
  $results += Invoke-RemoteTask -Computer $pc -TaskName $taskName -Credential $Credential
}

foreach ($result in $results) {
  if ($result.Success) {
    Write-Host "$($result.Computer): $($result.Task) OK"
  } else {
    Write-Host "$($result.Computer): $($result.Task) FAILED (exit $($result.ExitCode))"
    if ($result.Output) {
      Write-Host $result.Output
    }
  }
}
