DadBoard

DadBoard is a Windows LAN orchestration app for multi-PC family gaming. It provides a leader dashboard, per-PC agents, and a reliable self-update pipeline.

Architecture (locked)
- DadBoard.exe: runtime + tray/dashboard/leader/agent; no update logic.
- DadBoardUpdater.exe: decides updates, downloads payloads, invokes Setup.
- DadBoardSetup.exe: installer/executor only (stop/wait/unlock/replace/restart).

Install path
- %LOCALAPPDATA%\Programs\DadBoard\

Updater status store
- %LOCALAPPDATA%\DadBoard\Updater\last_result.json

Stable status schema (current)
- schema_version (int)
- timestamp_utc (ISO 8601)
- invocation (interactive|silent|scheduled|manual|triggered)
- channel (nightly|stable)
- installed_version
- latest_version
- action (none|checked|downloaded|invoked_setup|updated|repair|failed)
- success (bool)
- exit_code (int)
- error_code (string)
- error_message (string)
- log_path
- payload_path (optional)
- setup_exit_code (optional)
- duration_ms (optional)
- manifest_url

Updater exit codes
- 0 = success (up-to-date or update applied)
- 2 = invalid arguments
- 3 = network failure (manifest fetch)
- 4 = download failure (payload)
- 5 = setup invocation failure
- 6 = setup failed (non-zero)
- 7 = status store write failure
- 8 = unknown failure

Updater CLI examples
- Check now (interactive UI):
  - DadBoardUpdater.exe check --interactive
- Check silently:
  - DadBoardUpdater.exe check --silent --channel nightly
- Triggered run (no networking changes here, just a CLI entrypoint):
  - DadBoardUpdater.exe trigger --reason "leader-run" --channel nightly
- Schedule daily checks with jitter:
  - DadBoardUpdater.exe schedule install --channel nightly --time 03:00 --jitter-min 30
  - DadBoardUpdater.exe schedule status
  - DadBoardUpdater.exe schedule remove

Release baseline note
Releases prior to v0.1.0.1 may include binaries stamped as 1.0.0+<sha>, which breaks semver update ordering. Use v0.1.0.1 as the first correct-by-default baseline for update testing.
