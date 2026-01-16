DadBoard Roadmap (Current Direction)

0) Product North Star
- One app installed on each PC. Any PC can become Leader. The Leader dashboard orchestrates game launches with per-PC readiness and results.
- Later: DadVoice and Floor Gate become modules inside the same product (not separate apps).

1) Locked decisions (do not reopen unless explicitly stated)
1.1 Architecture / ownership boundaries (hard-locked)
- DadBoard.exe: tray + dashboard + leader/agent runtime; no update logic or downloads; only launches Updater.
- DadBoardUpdater.exe: decides updates, downloads payload, invokes Setup; no file replacement.
- DadBoardSetup.exe: install/uninstall/repair; stop/wait/unlock/replace/restart; shortcuts/registry/UAC; no scheduling or update decisions.

1.2 Update source strategy (locked)
- Option B1: GitHub Actions produces release assets; Leader mirrors/caches and serves LAN; Agents pull from Leader with GitHub fallback.

1.3 Leader-triggered updater orchestration (locked)
- RPC-style push from Leader to Agents; reuse existing comms.
- Progress UX: start/end only, no realtime percent.
- Agent responses: ACK on start + final success/fail with failure class and log reference.
- Result contract includes version_before/version_after; persist last run only (leader + agent).
- Single leader only; Co-leader (future) stays on roadmap.

1.4 Floor Control Gate (voice gating) decisions (locked)
- Gate level: 5% (duck, not mute).
- Roles: Leader / Co-Captain / Normal; Leader + Co-Captain overlap allowed.
- When neither leader nor co-captain is talking: exactly one Normal holds the floor (first-come).
- Smoothing/lease to prevent pumping; fast drop + slower restore.

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

Phase 4.2 -- Agent Update Exit Guard (DONE)
- Agent no longer exits after updater runs if no update was applied (up-to-date check).
- Shutdown only when updater reports updated/repair.

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
- Agent persists last launch state + error_class in agent_state.json.
- Diagnostics shows last launch state/message from agent_state.json.

What needs to be done next

Phase 6 (Game Launch MVP) is active.

After Phase 6:
- Option A -- Game launch polish (nice-to-have)
  - Make error classes more discoverable in the UI
  - Add a compact per-agent launch summary
- Option B -- Leader-triggered rollouts enhancements (nice-to-have)
  - Co-leader support (future)
  - Aggregated rollout reporting polish

3) Voice workstream (DadVoice + Floor Gate) -- planned
Phase V1 -- DadVoice skeleton (design-first)
- Define interfaces/contracts + status events; no DSP yet.

Phase V2 -- Floor Control Gate (policy + tray agent)
- Standalone tray agent module inside DadBoard repo.
- Mic scalar control (baseline + gate to 0.05 + restore).
- Role selection UI + status window.
- Logging + compact status.json.
- Scheduled task install script for gate agent.
- Out of scope: heavy audio routing/virtual devices, game launching, invite logic, Mumble server changes.

4) Known blockers / risk flags
- Versioning mismatch (EXE 1.0.0+<sha> vs manifest 0.1.0) can cause "no update" decisions.
- Nightly/Release artifacts are source of truth; no local publish exe dependence for updates.
