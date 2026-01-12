# DadBoard - Master Roadmap, Guardrails and Decision Tree (Cursor Execution Guide)

> **Project:** DadBoard
> **Goal:** One unified orchestration app for multi-PC family gaming:
> - Remote launch and readiness
> - Voice coordination
> - Mic floor control
> - Minimal friction, maximum reliability

This document is the source of truth for how work proceeds.
Cursor should follow this strictly.

---

## 0. GLOBAL RULES (NON-NEGOTIABLE)

### 0.1 Phase Discipline
- Only work on the current phase unless explicitly instructed.
- If an idea belongs to a future phase:
  - Document it in `docs/ideas.md`
  - Do not implement early.

### 0.2 Packaging Rule (ALWAYS)
At the end of every completed task:
1. Publish single-file, self-contained, win-x64 EXE.
2. If DadBoard is running and blocks publish/install:
   - You are allowed to terminate it automatically.
3. Commit and push.
4. State:
   - Commit hash
   - What changed
   - How to test

### 0.3 Reliability > Cleverness
- No silent failures.
- All errors must surface:
  - In UI (brief)
  - In logs (detailed)
- UI must never freeze due to long work on UI thread.

### 0.4 Leader vs UI Rule (Critical)
- Leader is a background state.
- Dashboard is only a view.
- Closing dashboard must NEVER disable leader.
- Only explicit "Disable Leader" stops it.

### 0.5 Config and Storage Rules
- Agent config:
  `%LOCALAPPDATA%\\DadBoard\\Agent\\agent.config.json`
- Leader config:
  `%LOCALAPPDATA%\\DadBoard\\Leader\\leader.config.json`
- Logs and diagnostics (preferred):
  `C:\\ProgramData\\DadBoard\\logs\\`
  `C:\\ProgramData\\DadBoard\\diag\\`
- If ProgramData fails:
  - Fallback to LocalAppData automatically
  - Never crash due to permissions

---

## 1. ARCHITECTURE OVERVIEW

### 1.1 Agent (runs on every PC)
- UDP broadcast discovery ("hello")
- WebSocket server (Kestrel, `0.0.0.0`)
- Executes commands (launch, future voice control)
- Always-on background tray app

### 1.2 Leader (enabled on exactly one PC at a time)
- UDP discovery listener
- WS client to agents
- Orchestrates commands and readiness
- Can run headless (tray) or with dashboard UI

### 1.3 Installation and Update Model
- Three executables with clean boundaries:
  - `DadBoard.exe` (app runtime + tray/dashboard/leader/agent; no update logic)
  - `DadBoardUpdater.exe` (decides updates, downloads payloads, launches Setup)
  - `DadBoardSetup.exe` (installer/executor; stop/wait/unlock/replace/restart)
- Canonical install path:
  - `%LOCALAPPDATA%\\Programs\\DadBoard`
- No PS1 required for normal use

---

## 2. PHASE ROADMAP (FULL)

---

## PHASE 1 - SPINE MVP (DONE / STABILIZING)

### Goal
Prove the orchestration spine works end-to-end.

### Delivered
- Agent + Leader
- UDP discovery
- WebSocket command channel (Kestrel)
- Remote "Open Notepad" works
- Single-file installer exists

### Remaining cleanup
- Leader enable/disable independent of dashboard
- Dashboard close hides window only
- Timer/UI disposal safety

### Exit Criteria
- Two PCs discover each other
- Remote command success + intentional failure handled cleanly
- Closing dashboard does NOT stop leader

---

## PHASE 2 - UX + RELIABILITY HARDENING (DONE)

### Phase 2.0 - Operator Control
**Goal:** Recover gracefully from hiccups.

Deliverables:
- Launch on Selected PC
- Launch on All
- Optional: Launch on Online Only
- Grid shows:
  - Online/offline
  - Command status
  - Last result + last error
- Right-click context menu on PC:
  - Launch on this PC
  - Test Notepad
  - Copy IP / pcId
  - View last error

Exit:
- One PC hiccup does not require restarting everything

---

### Phase 2.1 - Leader Lifecycle Stability
**Goal:** Make leader feel persistent and calm.

Deliverables:
- Enable Leader = background service
- Open Dashboard = show window
- Close dashboard = hide
- Tray:
  - Open Dashboard
  - Disable Leader
  - Exit App

Exit:
- Leader survives UI open/close cycles

---

### Phase 2.2 - Reconnect + Aging
**Goal:** Handle restarts naturally.

Deliverables:
- WS reconnect on agent restart
- Agents age out cleanly if offline
- No UI hangs from refresh timers

Exit:
- Reboot an agent PC -> leader recovers automatically

---

## PHASE 3 - GAME LAUNCH MVP (PAUSED)

### Goal
Replace "Notepad" with real games.

Deliverables:
- Game list (config or hardcoded initially)
- Steam URI or exe launch
- Launch on selected / all / online
- Per-PC success/failure reporting

Non-goals:
- No invites yet
- No voice integration yet

Exit:
- One click launches a game across PCs reliably

---

## PHASE 4 - VOICE SKELETON (NO DSP YET)

### Goal
Define structure without hard audio work.

Deliverables:
- DadVoice module scaffold
- Start/Stop/Ready/Error lifecycle
- No mic capture, no DSP
- Clean seams for later integration

Exit:
- Voice subsystem can be started/stopped cleanly

---

## PHASE 5 - FLOOR GATE (STANDALONE)

### Goal
Solve mic "doubling" in isolation.

Deliverables:
- GateAgent tray app
- Roles:
  - Leader
  - Co-captain
  - Normal
- 5 percent volume floor gating
- LAN coordination
- Status output (`status.json` or IPC)

Important:
- Must run independently of DadBoard initially

Exit:
- Mic floor control works without DadBoard integration

---

## PHASE 6 - INTEGRATION (VOICE + FLOOR GATE)

### Goal
Combine the superpowers.

Deliverables:
- DadBoard controls voice + floor gate lifecycle
- Leader controls mic priority
- Clear status surfaced in dashboard

Exit:
- One app orchestrates launch + voice + mic control

---

## PHASE 3.5 - SELF-UPDATE SYSTEM (OPTION B1) (DONE)

Delivered:
- GitHub Actions builds release assets:
  - `DadBoard.exe`
  - `DadBoardSetup.exe`
  - `DadBoardUpdater.exe`
  - `DadBoard-<version>.zip`
  - `latest.json`
- Nightly + Stable channels (default: Nightly)
- Leader mirrors GitHub releases over LAN and serves updates locally
- Agents pull from Leader with GitHub fallback
- Update lifecycle reporting survives restarts (requested → updating → restarting → updated/failed)
- Diagnostics shows:
  - Channel
  - Resolved manifest URL
  - Installed vs available versions
  - Update decision
- Reset Update Failures UX + backend

## PHASE 3.6 - VERSIONING CORRECTNESS (DONE)

Delivered:
- Git tag is single source of truth
- Version stamping aligned across:
  - ProductVersion
  - FileVersion
  - InformationalVersion
  - `latest.json.latest_version`
- SemVer comparison uses numeric ordering and ignores build metadata
- Nightly prerelease SemVer uses `x.y.z-nightly.N`
- Baseline correction: `v0.1.0.1` as first correct-by-default baseline

## PHASE 3.7 - INSTALLER AND UPDATE HARDENING (DONE)

Delivered:
- Setup is executor-only (payload path required; no network/manifest logic)
- Updater downloads payload and invokes Setup with `repair --payload <zip>`
- Auto-unblock downloaded updater/setup binaries (removes Mark of the Web)
- Tray update path runs Updater
- Desktop shortcut enforced on install/repair
- Setup CLI contract documented (`docs/setup-cli.md`)

## PHASE 3.8 - APP UPDATE DELEGATION (DONE)

Delivered:
- `DadBoard.exe` no longer performs update checks or downloads
- Tray delegates updates to `DadBoardUpdater.exe` (check/repair)
- Updater writes a status file (`%LOCALAPPDATA%\\DadBoard\\Updater\\last_result.json`)
- Diagnostics reads updater status instead of performing update decisions
- Updater UI only auto-runs when launched with `--auto`; manual open stays idle
- Update status dialog is copyable (text box + Copy button)

## PHASE 7 - POLISH AND QUALITY OF LIFE

Examples:
- Remote install
- Auto-update
- Theming / modern UI
- Per-user profiles
- Notifications / sounds

Only after core is rock solid.

---

## 3. DECISION TREE (DEBUGGING AND CHANGES)

### Install issues
- Installer closes silently -> add success screen
- Installer hangs -> check install log + readiness handshake
- Agent crashes on startup -> check config path and permissions

### Networking
- Discovery but no WS -> firewall or wrong bind address
- WS but no command -> agent execution path

### UI
- Closing dashboard causes crash -> timers not stopped or hide vs close bug
- Grid crash -> ensure columns exist, guard RefreshGrid

---

## 4. ACCEPTANCE TESTS (REFERENCE)

### Spine
- Two PCs, discovery works
- Remote Notepad opens
- Intentional failure reports error

### UX
- Launch on selected PC works
- Leader survives UI close
- Tray always shows correct state

### Stability
- Restart agent PC -> reconnect
- No duplicate tray icons
- No "Not Responding" windows

---

## 5. CURRENT NEXT TASK

**Phase 2.0 - Operator Control**
- Add Launch on Selected PC
- Add context menu
- Improve grid visibility

Follow packaging rules when done.
