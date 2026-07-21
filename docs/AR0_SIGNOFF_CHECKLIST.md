# AR-0 Sign-off Checklist

This is the final adapter/runtime planning checkpoint.

Status: **AR-0 approved. Master planning lock is lifted; AR-1 may begin under E1.**

The authoritative runtime/adapter contract is `ADAPTER_RUNTIME_ROADMAP.md`.

## Approved first-release decisions

| Area | Approved decision |
|---|---|
| Desktop lifetime | One user-session desktop process with tray/background lifetime; no Windows Service |
| Tray visibility | Tray is present whenever Steward process is running |
| Device concurrency | One active writable Steward-managed session per desktop/device |
| Lifecycle ownership | Runtime owns generic orchestration; adapter owns game evidence and safe capture |
| Internal state | Detailed runtime phases map to the fixed UI state vocabulary |
| Recovery naming | Existing internal `RecoveryPending` may remain if useful; UI/runtime meaning is `Recovery needed` |
| Session evidence | Structured adapter facts; PID is evidence input, not universal session definition |
| Pre-launch cancellation | Controlled temporary work may be discarded only before gameplay is proven and cleanup ownership is known |
| Post-launch failure/cancel | Preserve recovery evidence; stop safely when supported; never abandon silently |
| Stop and Save | Available only when adapter proves safe hosted stop/capture |
| Close/update | Hide to tray; guard Quit; defer self-update; scan recovery on startup |
| Connectivity loss | Active session may continue; reservation becomes Uncertain; candidate remains local; no competing writer |
| Cache/materialization | Runtime owns cache/recovery lifecycle; adapter owns workspace layout/restore/capture/cleanup authority |
| Join capability | Generic result supports Steam/game-native automatic, adapter automatic, guided manual, unsupported |
| Factorio host | Temporary local host using validated game-native/process model; no permanent Steward host |
| Palworld host | Temporary dedicated-server session with readiness/graceful-save proof |
| Palworld identity | Portability proceeds only with explicit safe handling of game-specific identity limitation |
| Recovery actions | Retry recovery, Export recovery copy, Continue from last safe state when proven safe |

## Session evidence contract

Adapters must provide enough structured evidence to distinguish:
- launch requested;
- real local session started;
- hosted server ready;
- session still running;
- graceful stop requested;
- normal or unexpected session end;
- safe capture boundary;
- capture blocked/incomplete;
- recovery evidence preserved.

Evidence may include process IDs/start times, server readiness, shutdown responses, stable files/package checks, and adapter diagnostics.

## Capability result contract

Generic outcomes include:

```text
SupportedAutomatic
SupportedGuidedManual
Unsupported
BlockedByEnvironment
BlockedByIdentityLimitation
```

The UI exposes one generic action such as Join. Adapter-specific instructions/data are returned only when needed for guided manual operation. Core/UI never branch on game name.

## Final UI mapping

| Runtime meaning | UI term |
|---|---|
| safe/available | Ready |
| preparing/materializing/restoring/starting | Preparing |
| local writable session | Running |
| hosted writable session | Hosting |
| remote host not ready | Host is starting |
| remote active writer | Someone is playing |
| capture/store/verify/commit/finalize | Saving World |
| candidate preserved; remote handoff unresolved | Waiting to sync |
| capability/environment/identity problem | Action required |
| unresolved prior handoff | Recovery needed |

## AR-0 acceptance batch

Implementation/release evidence must verify:

1. desktop remains alive in user session while window is hidden;
2. tray remains visible whenever Steward process runs;
3. second managed writable session is rejected on same device;
4. fake adapter proves every lifecycle phase/failure transition;
5. pre-launch cancellation cleans only controlled temporary work;
6. post-launch failure preserves recovery evidence;
7. Stop and Save appears only with safe adapter capability;
8. launcher/process handoff does not end a session prematurely;
9. BE-D005 uncertainty keeps World unavailable to competing writers;
10. BE-D008 outage flow can enter Waiting to sync and safely revalidate;
11. guided manual Join is representable without game-name branches;
12. Factorio local/host/capture/replay is proven;
13. Palworld dedicated readiness/stop/capture/restore is proven;
14. application restart surfaces unresolved recovery before Ready;
15. PC A -> PC B -> PC A handoff is proven for both initial adapters before commercial release.

## AR-0 gate

Status: **complete and approved**.

AR-1 implementation is allowed under E1 and must remain inside this frozen contract.