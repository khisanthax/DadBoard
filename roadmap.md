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

3) Floor Control Gate Roadmap (current)
Phase 0 -- Locked decisions and architecture (DONE)
- Gate runs inside DadBoard.exe (no separate GateAgent exe).
- Two modes: Dashboard and Gate/Tray (--mode gate).
- VOX-friendly gating: reduce Windows mic level to 5%, never hard mute.
- Roles: Leader / Co-Captain / Normal.
- Priority rules:
  - Leader always allowed.
  - Co-Captain allowed over normals.
  - Leader + Co-Captain overlap allowed.
  - Normals: one-at-a-time when neither Leader nor Co-Captain is talking.
- Per-device baseline stored by IMMDevice.Id.
- UDP ports: 39555 discovery, 39566 gate, gate port configurable.

Phase 1 -- Core Gate Mode implementation (DONE)
- Gate/tray mode exists with role selection UI.
- NAudio/CoreAudio mic control + baseline restore.
- Role claims + deterministic floor selection.
- Calibration + Quick Test overlays.
- Status UI shows port + basic state.
- Docs updated; nightly published.

Phase 2 -- Operational hardening (NEXT, highest priority)
2.1 Gate status JSON telemetry (DONE)
- Goal: dashboard/diagnostics can see everything without opening the gate UI.
- Add gatePort (and discoveryPort) to the status JSON.
- Include: selected input device id/name, baseline volume, current applied volume, role/effective role, talking/allowed, floor owner, leader/co-captain ids, peerCount, lastPeerSeen, timestamps.
- Atomic writes + schema version.
- Acceptance: dashboard can read status JSON and show gate port/state; JSON updates reliably.

2.2 Setup scheduled task for auto-run at login (NEXT)
- Goal: gate mode runs on every PC automatically.
- Setup creates/updates scheduled task (idempotent):
  - DadBoard.exe --mode gate
  - trigger: at logon (user session)
- Ensure task updates path/args on reinstall.
- Ensure uninstall/remove disables/removes task appropriately.
- Gate logs at startup with version, selected device, bound port, baseline.
- Acceptance: reboot/logoff-logon shows tray every time on remote PCs; rerun installer doesn’t duplicate tasks.

2.3 Dashboard control of local gate (NEXT)
- Goal: on your main PC, you shouldn’t have to run two DadBoard processes.
- Dashboard can start/stop local gate subsystem.
- Implementation choice:
  - Preferred: dashboard hosts gate functionality when open.
  - Acceptable interim: single-instance gate process + dashboard attaches/controls.
- Acceptance: you can toggle local gate on/off from dashboard; no port/device fights.

Phase 3 -- Fleet deployment + profile push consistency (NEXT after Phase 2)
3.1 Standard Mumble profile push
- Goal: consistent VOX behavior across machines so 5% gating reliably prevents transmit.
- Define a standard mumble.conf baseline (VOX mode, max amplification, hold time, noise suppression choice).
- Add a setup step/script to deploy it to each PC (without copying identities/certs unintentionally).
- Acceptance: all PCs behave similarly; 5% gate never triggers VOX.

3.2 Remote calibration workflow (quality-of-life)
- Goal: handle quiet talkers without walking around.
- Trigger per-PC Calibrate Mic from dashboard (or at least expose clearly in tray).
- Save per-device baseline; clamp 10–95%.
- Add a Quick Test per PC to validate VOX doesn’t trigger at 5%.
- Acceptance: you can standardize levels for kids/adults quickly; fewer manual tweaks.

Phase 4 -- Diagnostics and reliability (later but valuable)
- Add gate status + logs into an existing diagnostics bundle (single-click gather).
- Add basic fault reporting:
  - if peerCount drops, show warning.
  - if device disappears, auto-fallback to default recording device and warn.
- Add a simple simulation/test mode for deterministic floor logic verification.

4) Known blockers / risk flags
- Versioning mismatch (EXE 1.0.0+<sha> vs manifest 0.1.0) can cause "no update" decisions.
- Nightly/Release artifacts are source of truth; no local publish exe dependence for updates.
