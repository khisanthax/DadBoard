DadBoard Setup CLI (Phase 2)

DadBoardSetup.exe is a pure executor. It does not fetch manifests or decide updates.
It only performs install/repair/uninstall/stop actions using explicit local inputs.

Usage

Install or repair (payload required)
  DadBoardSetup.exe install --payload "<path-to-zip>" [--log "<path>"] [--silent]
  DadBoardSetup.exe repair  --payload "<path-to-zip>" [--log "<path>"] [--silent]

Uninstall
  DadBoardSetup.exe uninstall [--log "<path>"] [--silent]

Register desktop shortcuts
  DadBoardSetup.exe register-shortcuts [--log "<path>"]

Stop the running app (best-effort)
  DadBoardSetup.exe stop-app [--wait-ms 15000] [--log "<path>"]

Notes
  - --payload must be a local file path. file:// URIs are allowed.
  - Setup never contacts the network.
  - The updater is responsible for downloading the payload zip and invoking Setup.

Exit codes
  0 = success
  2 = operation failed
  3 = logging failure
  4 = invalid arguments (missing payload)

Smoke tests
  1) Offline: run install with a valid payload zip and verify success.
  2) Missing payload: install without --payload should return exit code 4.
  3) Uninstall should work without network access.
