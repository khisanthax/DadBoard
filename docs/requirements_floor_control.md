# Floor Control Gate requirements (legacy fork)

## Purpose
Provide a floor-control mode so a leader (and optional co-captain) can speak clearly over others without fully muting teammates.

## Behavior
- One leader PC at a time; leadership can move between PCs.
- Optional co-captain with elevated priority.
- When the floor is held by leader/co-captain, others are reduced to ~5-10% volume, not hard muted.
- When floor is released, volumes return to normal preset.

## Constraints
- User-triggered (no always-on agent).
- Prefer safe, explicit actions and timeouts over automatic continuous monitoring.
- Integrate as a feature of the DadVoice subsystem.

## Networking (current)
- UDP 39555 is reserved for DadBoard discovery.
- Floor Control Gate uses UDP 39566 by default (configurable via gate settings).

## Gate status + logs
- Status JSON: `C:\ProgramData\DadBoard\Gate\status.json` (updates ~1s).
- Gate log: `C:\ProgramData\DadBoard\logs\gate.log`.

## Scheduled task (gate mode)
- Task name: `DadBoard Gate`
- Trigger: At logon (user session)
- Action: `DadBoard.exe --mode gate --minimized --no-first-run`
- Idempotent: setup updates the task on install/repair and removes on uninstall.
