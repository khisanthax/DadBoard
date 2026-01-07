# DadBoard – Master Roadmap, Guardrails & Decision Tree (Cursor Execution Guide)

> **Project:** DadBoard
> **Goal:** One unified orchestration app for multi-PC family gaming:
> - Remote launch & readiness
> - Voice coordination
> - Mic floor control
> - Minimal friction, maximum reliability

This document is the **source of truth** for how work proceeds.
Cursor should follow this strictly.

---

## 0. GLOBAL RULES (NON-NEGOTIABLE)

### 0.1 Phase Discipline
- Only work on the **current phase** unless explicitly instructed.
- If an idea belongs to a future phase:
  - Document it in `docs/ideas.md`
  - Do **not** implement early.

### 0.2 Packaging Rule (ALWAYS)
At the end of **every completed task**:
1. Publish **single-file**, **self-contained**, **win-x64** EXE.
2. If DadBoard is running and blocks publish/install:
   - You are allowed to **terminate it automatically**.
3. Commit + push.
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
- **Leader is a background state.**
- **Dashboard is only a view.**
- Closing dashboard must NEVER disable leader.
- Only explicit “Disable Leader” stops it.

### 0.5 Config + Storage Rules
- **Agent config:**
  `%LOCALAPPDATA%\DadBoard\Agent\agent.config.json`
- **Leader config:**
  `%LOCALAPPDATA%\DadBoard\Leader\leader.config.json`
- **Logs / diagnostics (preferred):**
  `C:\ProgramData\DadBoard\logs\`
  `C:\ProgramData\DadBoard\diag\`
- If ProgramData fails:
  - Fallback to LocalAppData automatically
  - Never crash due to permissions

---

## 1. ARCHITECTURE OVERVIEW

### 1.1 Agent (runs on every PC)
- UDP broadcast discovery (“hello”)
- WebSocket server (Kestrel, `0.0.0.0`)
- Executes commands (launch, future voice control)
- Always-on background tray app

### 1.2 Leader (enabled on exactly one PC at a time)
- UDP discovery listener
- WS client to agents
- Orchestrates commands and readiness
- Can run headless (tray) or with dashboard UI

### 1.3 Installation Model
- ONE executable (`DadBoard.exe`)
- User runs EXE → chooses Install
- Installer:
  - Elevates once
  - Installs to Program Files
  - Sets up agent auto-start
  - Shows progress + success confirmation
- No PS1 required for normal use

---

## 2. PHASE ROADMAP (FULL)

---

## PHASE 1 — SPINE MVP (DONE / STABILIZING)

### Goal
Prove the orchestration spine works end-to-end.

### Delivered
- Agent + Leader
- UDP discovery
- WebSocket command channel (Kestrel)
- Remote “Open Notepad” works
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

## PHASE 2 — UX + RELIABILITY HARDENING (CURRENT)

### Phase 2.0 — Operator Control
**Goal:** Recover gracefully from hiccups.

Deliverables:
- Launch on **Selected PC**
- Launch on **All**
- Optional: Launch on **Online Only**
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
- One PC hiccup doesn’t require restarting everything

---

### Phase 2.1 — Leader Lifecycle Stability
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

### Phase 2.2 — Reconnect + Aging
**Goal:** Handle restarts naturally.

Deliverables:
- WS reconnect on agent restart
- Agents age out cleanly if offline
- No UI hangs from refresh timers

Exit:
- Reboot an agent PC → leader recovers automatically

---

## PHASE 3 — GAME LAUNCH MVP

### Goal
Replace “Notepad” with real games.

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

## PHASE 4 — VOICE SKELETON (NO DSP YET)

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

## PHASE 5 — FLOOR GATE (STANDALONE)

### Goal
Solve mic “doubling” in isolation.

Deliverables:
- GateAgent tray app
- Roles:
  - Leader
  - Co-captain
  - Normal
- 5% volume floor gating
- LAN coordination
- Status output (`status.json` or IPC)

Important:
- Must run independently of DadBoard initially

Exit:
- Mic floor control works without DadBoard integration

---

## PHASE 6 — INTEGRATION (VOICE + FLOOR GATE)

### Goal
Combine the superpowers.

Deliverables:
- DadBoard controls voice + floor gate lifecycle
- Leader controls mic priority
- Clear status surfaced in dashboard

Exit:
- One app orchestrates launch + voice + mic control

---

## PHASE 7 — POLISH & QUALITY OF LIFE

Examples:
- Remote install
- Auto-update
- Theming / modern UI
- Per-user profiles
- Notifications / sounds

Only after core is rock solid.

---

## 3. DECISION TREE (DEBUGGING & CHANGES)

### Install issues
- Installer closes silently → add success screen
- Installer hangs → check install log + readiness handshake
- Agent crashes on startup → check config path & permissions

### Networking
- Discovery but no WS → firewall or wrong bind address
- WS but no command → agent execution path

### UI
- Closing dashboard causes crash → timers not stopped or hide vs close bug
- Grid crash → ensure columns exist, guard RefreshGrid

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
- Restart agent PC → reconnect
- No duplicate tray icons
- No “Not Responding” windows

---

## 5. CURRENT NEXT TASK

**Phase 2.0 — Operator Control**
- Add Launch on Selected PC
- Add context menu
- Improve grid visibility

Follow packaging rules when done.
