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
