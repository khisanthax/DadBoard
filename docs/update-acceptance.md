# DadBoard Update Acceptance Test (Baseline v0.1.0.1 -> v0.1.1)

## One-bundle PowerShell (copy/paste ONLY this block)
```powershell
Write-Host "=== DadBoard Baseline Setup v0.1.0.1 ==="

$InstallDir = "$env:LOCALAPPDATA\Programs\DadBoard"
$ExePath    = Join-Path $InstallDir "DadBoard.exe"
$ZipUrl     = "https://github.com/khisanthax/DadBoard/releases/download/v0.1.0.1/DadBoard-0.1.0.1.zip"
$ZipPath    = Join-Path $env:TEMP "DadBoard-0.1.0.1.zip"
$Manifest11 = "https://github.com/khisanthax/DadBoard/releases/download/v0.1.1/latest.json"

Write-Host "[1/7] Stopping DadBoard processes..."
Get-Process -Name "DadBoard*" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

Write-Host "[2/7] Removing install dir (retry up to 3x)..."
for ($i = 1; $i -le 3; $i++) {
  if (Test-Path $InstallDir) {
    try {
      Remove-Item $InstallDir -Recurse -Force -ErrorAction Stop
      break
    } catch {
      Write-Host "  retry $i failed: $($_.Exception.Message)"
      Start-Sleep -Seconds 1
    }
  }
}
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

Write-Host "[3/7] Downloading baseline zip..."
Invoke-WebRequest -Uri $ZipUrl -OutFile $ZipPath -UseBasicParsing

Write-Host "[4/7] Extracting zip to install dir..."
Expand-Archive -Path $ZipPath -DestinationPath $InstallDir -Force

Write-Host "[5/7] Installed version info:"
if (Test-Path $ExePath) {
  $info = (Get-Item $ExePath).VersionInfo
  Write-Host "ProductVersion=$($info.ProductVersion)"
  Write-Host "FileVersion=$($info.FileVersion)"
} else {
  Write-Host "DadBoard.exe not found at $ExePath"
}

Write-Host "[6/7] Checking stable manifest v0.1.1..."
$manifest = Invoke-RestMethod -Uri $Manifest11
Write-Host "latest_version=$($manifest.latest_version)"

Write-Host "[7/7] NEXT ACTION:"
Write-Host "Run DadBoard.exe, open Diagnostics, set channel Stable and manifest to $Manifest11, confirm Available 0.1.1, then click Update."
```

## Baseline note
Releases prior to v0.1.0.1 may contain binaries stamped as 1.0.0+<sha>, which breaks semver update ordering. Use v0.1.0.1 as the first correct-by-default baseline.

## Expected output
- FileVersion prints `0.1.0.1` (or `0.1.0.1.0`). ProductVersion may include `+<sha>` and is OK.
- Manifest latest_version prints `0.1.1`.

## Final step
1) Run `DadBoard.exe` from `%LOCALAPPDATA%\Programs\DadBoard\`.
2) In Diagnostics, set Update Channel to **Stable**.
3) Set manifest override to:
   `https://github.com/khisanthax/DadBoard/releases/download/v0.1.1/latest.json`
4) Confirm Available Version shows `0.1.1`, then click Update.
