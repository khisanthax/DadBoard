# DadBoard Update Acceptance Test (Baseline v0.1.0.1 -> v0.1.1)

This document verifies the stable update path using a corrected baseline release.

## Baseline note
Older releases prior to v0.1.0.1 may contain binaries stamped as 1.0.0+<sha>, which breaks semver update ordering. The v0.1.0.1 release is the first correct-by-default baseline.

## Test steps
1) Install baseline v0.1.0.1
   - Download the v0.1.0.1 release assets from GitHub.
   - Install using DadBoardSetup.exe OR extract DadBoard-0.1.0.1.zip to:
     `%LOCALAPPDATA%\Programs\DadBoard\`

2) Verify the installed ProductVersion
```powershell
(Get-Item "$env:LOCALAPPDATA\Programs\DadBoard\DadBoard.exe").VersionInfo.ProductVersion
```
Expected: `0.1.0.1` (or `0.1.0.1.0` depending on Windows display).

3) Point manifest to stable v0.1.1
Use the stable manifest URL:
`https://github.com/khisanthax/DadBoard/releases/download/v0.1.1/latest.json`

4) Trigger update
- From the Leader UI, use Update Selected / Update All.
- Or wait for the periodic check.

5) Verify update to v0.1.1
```powershell
(Get-Item "$env:LOCALAPPDATA\Programs\DadBoard\DadBoard.exe").VersionInfo.ProductVersion
```
Expected: `0.1.1` (or `0.1.1.0`).

## Manifest verification
```powershell
Invoke-RestMethod "https://github.com/khisanthax/DadBoard/releases/download/v0.1.1/latest.json"
```
Expected: `latest_version` == `0.1.1`.
