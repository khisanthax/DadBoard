[CmdletBinding()]
param(
  [switch]$SkipQuiet,
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

if (-not $tasks.start) {
  Write-Error "Missing tasks.start in config.json."
  exit 1
}

$applyQuiet = $false
if ($config.applyQuietBeforeStart) {
  $applyQuiet = $true
}
if ($SkipQuiet) {
  $applyQuiet = $false
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
  if ($applyQuiet -and $tasks.quiet) {
    $results += Invoke-RemoteTask -Computer $pc -TaskName $tasks.quiet -Credential $Credential
  }

  $results += Invoke-RemoteTask -Computer $pc -TaskName $tasks.start -Credential $Credential
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
