# Phase 6 RPC Notes

Phase 6 (Game Launch MVP) reuses the existing Phase 5 RPC flow:

- Leader RPC send: `modules/Spine/Leader/LeaderService.cs` (`SendLaunchCommand`, `SendLaunchAppId`).
- Agent RPC handler: `modules/Spine/Agent/AgentService.cs` (`HandleEnvelope`, `ExecuteLaunchCommand`).
- Shared contracts: `modules/Spine/Shared/Protocol.cs` (`LaunchGameCommand`, `AckPayload`, `StatusPayload`).

