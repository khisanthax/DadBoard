$ErrorActionPreference = 'Stop'
$repo = 'khisanthax/DadBoard'
$assetsJson = gh release view nightly --repo $repo --json assets | ConvertFrom-Json
$nightlyNums = @()
foreach ($asset in $assetsJson.assets) {
    if ($asset.name -match 'DadBoard-0\.1\.0-nightly\.(\d+)\.zip') {
        $nightlyNums += [int]$Matches[1]
    }
}
if ($nightlyNums.Count -eq 0) { throw 'Could not determine latest nightly number.' }
$next = ($nightlyNums | Measure-Object -Maximum).Maximum + 1
$baseVersion = '0.1.0'
$version = "$baseVersion-nightly.$next"
$shortSha = (git rev-parse --short=7 HEAD).Trim()
$assemblyVersion = "$baseVersion.0"
$infoVersion = "$version+$shortSha"

Write-Host "Publishing version $version ($shortSha)"
$env:VERSION = $version
$env:BASE_VERSION = $baseVersion
$env:ASSEMBLY_VERSION = $assemblyVersion
$env:FILE_VERSION = $assemblyVersion
$env:INFO_VERSION = $infoVersion

# Publish App
pushd modules/Spine/App
 dotnet publish -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=true /p:Version=$env:VERSION /p:AssemblyVersion=$env:ASSEMBLY_VERSION /p:FileVersion=$env:FILE_VERSION /p:InformationalVersion=$env:INFO_VERSION
popd

# Publish Setup
pushd modules/Spine/Setup
 dotnet publish -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=true /p:Version=$env:VERSION /p:AssemblyVersion=$env:ASSEMBLY_VERSION /p:FileVersion=$env:FILE_VERSION /p:InformationalVersion=$env:INFO_VERSION
popd

# Publish Updater
pushd modules/Spine/Updater
 dotnet publish -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=true /p:Version=$env:VERSION /p:AssemblyVersion=$env:ASSEMBLY_VERSION /p:FileVersion=$env:FILE_VERSION /p:InformationalVersion=$env:INFO_VERSION
popd

# Validate version stamps
$expected = $env:VERSION
Write-Host "Expected version: $expected"
$app = 'modules/Spine/App/bin/Release/net8.0-windows/win-x64/publish/DadBoard.exe'
$setup = 'modules/Spine/Setup/bin/Release/net8.0-windows/win-x64/publish/DadBoardSetup.exe'
$updater = 'modules/Spine/Updater/bin/Release/net8.0-windows/win-x64/publish/DadBoardUpdater.exe'
$appInfo = (Get-Item $app).VersionInfo
$setupInfo = (Get-Item $setup).VersionInfo
$updaterInfo = (Get-Item $updater).VersionInfo
Write-Host "DadBoard.exe ProductVersion=$($appInfo.ProductVersion) FileVersion=$($appInfo.FileVersion)"
Write-Host "DadBoardSetup.exe ProductVersion=$($setupInfo.ProductVersion) FileVersion=$($setupInfo.FileVersion)"
Write-Host "DadBoardUpdater.exe ProductVersion=$($updaterInfo.ProductVersion) FileVersion=$($updaterInfo.FileVersion)"
if (-not $appInfo.ProductVersion.StartsWith($expected)) { throw 'DadBoard.exe ProductVersion mismatch.' }
if (-not $setupInfo.ProductVersion.StartsWith($expected)) { throw 'DadBoardSetup.exe ProductVersion mismatch.' }
if (-not $updaterInfo.ProductVersion.StartsWith($expected)) { throw 'DadBoardUpdater.exe ProductVersion mismatch.' }
if (-not $appInfo.FileVersion.StartsWith($env:ASSEMBLY_VERSION)) { throw 'DadBoard.exe FileVersion mismatch.' }
if (-not $setupInfo.FileVersion.StartsWith($env:ASSEMBLY_VERSION)) { throw 'DadBoardSetup.exe FileVersion mismatch.' }
if (-not $updaterInfo.FileVersion.StartsWith($env:ASSEMBLY_VERSION)) { throw 'DadBoardUpdater.exe FileVersion mismatch.' }

# Build release assets
$publishApp = 'modules/Spine/App/bin/Release/net8.0-windows/win-x64/publish'
$publishSetup = 'modules/Spine/Setup/bin/Release/net8.0-windows/win-x64/publish'
$publishUpdater = 'modules/Spine/Updater/bin/Release/net8.0-windows/win-x64/publish'
$payloadDir = 'release_payload'

if (Test-Path $payloadDir) { Remove-Item $payloadDir -Recurse -Force }
New-Item -ItemType Directory -Force $payloadDir | Out-Null
Copy-Item "$publishApp/DadBoard.exe" $payloadDir -Force
Copy-Item "$publishUpdater/DadBoardUpdater.exe" $payloadDir -Force
Copy-Item "$publishApp/DadBoard.exe" 'DadBoard.exe' -Force
Copy-Item "$publishSetup/DadBoardSetup.exe" 'DadBoardSetup.exe' -Force
Copy-Item "$publishUpdater/DadBoardUpdater.exe" 'DadBoardUpdater.exe' -Force

$zipName = "DadBoard-$version.zip"
if (Test-Path $zipName) { Remove-Item $zipName -Force }
Compress-Archive -Path "$payloadDir/*" -DestinationPath $zipName -Force

$token = [int][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$packageUrl = "https://github.com/$repo/releases/download/nightly/$zipName"
$latest = @{
  latest_version = $version
  package_url = $packageUrl
  force_check_token = $token
  min_supported_version = $baseVersion
} | ConvertTo-Json -Depth 5
Set-Content -Path 'latest.json' -Value $latest -Encoding utf8

# Validate nightly assets
$required = @(
  'DadBoard.exe',
  'DadBoardUpdater.exe',
  'DadBoardSetup.exe',
  'latest.json',
  $zipName
)
foreach ($file in $required) {
  if (-not (Test-Path $file)) { throw "Missing release asset: $file" }
}
$json = Get-Content latest.json -Raw | ConvertFrom-Json
Write-Host "latest.json latest_version=$($json.latest_version)"
if ($json.latest_version -ne $env:VERSION) { throw 'latest.json version mismatch.' }

# Upload to GitHub Release (nightly)
gh release upload nightly $zipName DadBoard.exe DadBoardUpdater.exe DadBoardSetup.exe latest.json --clobber --repo $repo

# Cleanup artifacts
Remove-Item $zipName, DadBoard.exe, DadBoardUpdater.exe, DadBoardSetup.exe, latest.json -Force
Remove-Item $payloadDir -Recurse -Force

Write-Host "Publish complete: $version"
