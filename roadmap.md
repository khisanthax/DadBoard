DadBoard Roadmap (Current Direction)

Phase 0 -- Wipe/Reset Utilities (DONE)
- Wipe-all script to eliminate mixed-install state and stale shortcuts/tasks/registry remnants.

Phase 1 -- Introduce Updater (Additive Only) (DONE)
- Added DadBoardUpdater.exe as a separate executable.
- Manual update check flow working via Updater calling Setup.

Phase 1.5 -- Nightly Correctness (DONE)
- Nightly must always publish:
  - DadBoard.exe
  - DadBoardUpdater.exe
  - DadBoardSetup.exe
  - payload zip
  - latest.json
- Version increments every Nightly.
- Verified end-to-end and treat Nightly as source of truth.

Phase 2 -- Trim Setup (Setup becomes pure executor) (DONE)
- Setup removed update decision logic.
- Setup only performs install/repair/uninstall + stop/wait/unlock/replace/restart + shortcuts/registry/UAC.
- Setup accepts explicit local payload path and returns meaningful exit codes.

Phase 3 -- Trim DadBoard (App delegates; no update logic) (DONE)
- DadBoard tray owns UX only and launches Updater for check/repair.
- Diagnostics reads updater status store instead of contacting GitHub.
- Legacy bootstrapper/update paths removed.
- Introduced last_result.json status store (initial schema).

Phase 4 -- Updater "Product-Grade" (DONE)
- Scheduled update checks:
  - schedule install/remove/status
  - jitter support
  - implemented via schtasks
- Stable/atomic status store:
  - schema_versioned last_result.json
  - atomic writes
  - backward-compatible load
- Stable exit codes + silent behavior:
  - consistent exit codes for network/download/setup failures
  - check --silent, trigger --reason, --invocation, --channel
- Diagnostics updated to read new schema.
- Docs updated: README + roadmap.
- Published and pushed: commit 8148fd6.

Phase 4.1 -- Post-Update Quiet Restart (DONE)
- Prevent Status window from popping after update.
- Setup restarts DadBoard in tray-only/minimized mode (quiet restart).
- Published and pushed: commit 52282d6.

Phase 5 -- Leader-Triggered Updater Runs (DONE)
- Leader commands selected agents to run DadBoardUpdater.exe.
- Agent sends ACK on start + final success/failure result with error class.
- Leader aggregates per-agent results (start/end only, no progress).
- Versions reported per agent: version_before and version_after (shown under Last Result).
- Persistence: last run only (agent status store + leader cached view).
- Co-leader support (future).

Phase 6 -- Game Launch MVP (ACTIVE)
- Manual agent selection + explicit Launch button in Games tab.
- Launch uses existing leader -> agent RPC with ACK + final status.
- Agents start via steam://run/<appId> or explicit exe path.
- Per-agent results shown inline (Succeeded / Failed with error class + message).
- Process wait/timeout still enabled for confirmation (running or timeout).

What needs to be done next

Phase 6 (Game Launch MVP) is active.

After Phase 6:
- Option A -- Game launch polish (nice-to-have)
  - Make error classes more discoverable in the UI
  - Add a compact per-agent launch summary
- Option B -- Leader-triggered rollouts enhancements (nice-to-have)
  - Co-leader support (future)
  - Aggregated rollout reporting polish
