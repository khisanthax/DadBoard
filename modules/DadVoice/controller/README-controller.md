# DadVoice Controller

## Overview
Run these scripts on your main PC to trigger scheduled tasks on each gaming PC.

## Configuration
Edit `controller\config.json`:
- `computers`: list of PC names or IPs.
- `tasks`: scheduled task names created by Setup-Client.
- `applyQuietBeforeStart`: if true, set quiet volume before starting Mumble.

## Usage
Start DadVoice (quiet first by default):
`powershell -ExecutionPolicy Bypass -File .\Start-DadVoice.ps1`

Start DadVoice without quiet preset:
`powershell -ExecutionPolicy Bypass -File .\Start-DadVoice.ps1 -SkipQuiet`

Set volume preset on all PCs:
`powershell -ExecutionPolicy Bypass -File .\Set-DadVoiceVolume.ps1 -Preset quiet`

Optional credentials (if needed):
```
$cred = Get-Credential
.\Start-DadVoice.ps1 -Credential $cred
.\Set-DadVoiceVolume.ps1 -Preset normal -Credential $cred
```

## Required permissions and firewall
- Use an account that is a local admin on each target PC.
- Windows firewall must allow:
  - Remote Scheduled Tasks Management (RPC)
  - File and Printer Sharing (SMB-In)
- Task Scheduler service must be running on each target PC.
- If using local (non-domain) accounts, remote UAC can block admin tokens. Either use identical admin accounts on all PCs or set `LocalAccountTokenFilterPolicy` to 1 on the targets.

## Connectivity tests
- Ping each PC: `ping PCNAME`
- RPC connectivity: `Test-NetConnection -ComputerName PCNAME -Port 135`
- Task exists: `schtasks /Query /S PCNAME /TN DadVoice_Start`
- Local task run: `schtasks /Run /TN DadVoice_Start`

## Troubleshooting
- Task not found: rerun `client\Setup-Client.ps1` and confirm task names match `config.json`.
- Access denied: verify admin rights, firewall rules, and remote UAC settings.
- Mumble not installed: check `%ProgramData%\DadVoice\setup.log` on the target PC.
- URL handler issues: run Mumble once on the target PC so the `mumble://` protocol registers.
