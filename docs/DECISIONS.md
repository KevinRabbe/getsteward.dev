# Design Decisions

This file records durable architectural decisions so future implementation work does not accidentally reverse them.

## D-001: The World is the product

**Decision:** The user-facing object is a World, not a save file, server, install folder, or modpack.

**Reason:** Users want to continue a shared playable reality. Save files, mods, versions, hosts, and launchers are implementation details required to reproduce that reality.

## D-002: Core must not know game-specific behavior

**Decision:** No game-specific branches in Core.

**Reason:** Adding support for one strange game must not make the universal product model more complex.

**Consequence:** All game-specific discovery, save handling, environment logic, launch behavior, and session observation belong behind `IGameAdapter`.

## D-003: Steam is infrastructure, not a game requirement

**Decision:** Supported games do not need to be Steam games.

**Reason:** The application may use Steam for some platform services while adapters support games installed or managed elsewhere.

**Consequence:** There is no mandatory `SteamAppId` in `IGameAdapter` or `World`.

## D-004: External ecosystems remain adapter details

**Decision:** CurseForge, Modrinth, Prism, Steam Workshop, custom launchers, and similar systems are not Core concepts.

**Reason:** The Core should not care where a game or mod came from.

## D-005: Environment and state are versioned separately

**Decision:** A World references an environment revision and a state revision independently.

**Reason:** Most play sessions change the save state without changing the game/mod environment.

**Example:** `E7 + S143 -> E7 + S144`.

## D-006: The manifest is authoritative; the fingerprint is disposable

**Decision:** `EnvironmentManifest` is the source of truth. `EnvironmentFingerprint` is only a comparison/cache aid.

**Reason:** A small canonical manifest can be compared cheaply without pretending that the entire game installation has been deeply verified.

## D-007: Do not routinely hash whole game installations

**Decision:** Full-install hashing is not part of the normal hot path.

**Reason:** Hashing tens of gigabytes repeatedly is expensive and unnecessary for ordinary environment comparison.

**Allowed uses:** explicit Verify/Repair, corruption investigation, first-download integrity, or targeted validation where justified.

## D-008: One canonical host at a time

**Decision:** Only one active canonical host may advance the shared canonical World.

**Reason:** Generic automatic merging of arbitrary game saves is unsafe or impossible.

**Consequence:** Other members join the host, request handoff, create a Sandbox, or Fork.

## D-009: Nobody permanently owns the host

**Decision:** Host ownership belongs to the active session, not permanently to a player.

**Reason:** The group owns the World.

## D-010: Host handoff is a controlled restart

**Decision:** Do not attempt live game-process migration.

**Flow:** save -> close -> capture -> commit -> restore on new host -> launch -> join.

**Reason:** This achieves the desired user experience with far less complexity and fewer failure modes.

## D-011: Temporary connectivity loss does not end a session

**Decision:** Losing Discord, P2P connectivity, or another transient channel must not automatically free the canonical host role.

**Reason:** The game/session lifecycle is a better authority for state ownership than incidental network connectivity.

## D-012: Adapters own import capture

**Decision:** `CaptureDetectedWorldAsync` belongs to `IGameAdapter`.

**Reason:** A game's save may be one ZIP, a directory tree, multiple files, a database, or launcher-managed state. The Core must not assume a universal file shape.

## D-013: Adapters own session-end observation

**Decision:** `WaitForSessionEndAsync` belongs to `IGameAdapter`.

**Reason:** Some launchers spawn, replace, or hand off processes. A generic Core `Process.WaitForExit` would encode a false universal assumption.

## D-014: Storage and live coordination are separate concerns

**Decision:** `IWorldStorage` and `IWorldSessionCoordinator` are separate boundaries.

**Reason:** Durable revision storage and transient host/session state have different lifecycles and may use different infrastructure.

**Example:** Steam Workshop/UGC could potentially back durable storage while Steam lobbies coordinate a live session.

## D-015: Start local before shared networking

**Decision:** Prove the complete local World lifecycle before adding remote storage and host coordination.

**Reason:** Networking should not hide errors in import, environment preparation, state capture, or revision logic.

## D-016: Build a narrow version of the final architecture

**Decision:** Avoid disposable prototype architecture.

**Reason:** The first implementation should be small, but its boundaries should survive later growth.

## D-017: Initial adapters are Factorio, 7 Days to Die, and Project Zomboid

**Decision:** Use three games that stress different adapter problems.

**Purpose:**

- Factorio: relatively clean first vertical slice and strong native mod synchronization behavior.
- 7 Days to Die: environment isolation and heavily modded setups.
- Project Zomboid: Workshop-heavy mod environment.

The first implementation focus remains Factorio until one complete lifecycle works end to end.

## D-018: Keep the product name replaceable

**Decision:** Repository/product naming is temporary and must not become an architectural dependency.

**Reason:** The project can be renamed later without changing namespaces, domain semantics, or platform contracts all at once.

## D-019: Each game adapter compiles independently

**Decision:** Each supported game has its own adapter project/assembly rather than sharing one monolithic game-adapter assembly.

**Reason:** Game-specific dependencies, platform SDKs, parsing libraries, and failure surfaces should remain isolated. Adding a dependency for one game must not become a dependency of every adapter.

## D-020: Canonical host ownership is enforced through the coordination port

**Decision:** Canonical play must acquire host ownership through `IWorldSessionCoordinator` before a session can advance the World.

**Reason:** A one-host rule that exists only in UI logic or documentation is not an invariant. The current local coordinator enforces the rule in-process; a future distributed coordinator can replace it without changing lifecycle semantics.

## D-021: State revision metadata is readable independently of payload bytes

**Decision:** `IWorldStorage` exposes state revision metadata separately from opening the opaque state payload.

**Reason:** Core must be able to validate adapter identity, lineage, and future history/restore metadata without interpreting or downloading the entire game-specific payload first.

## D-022: Temporary captured-package ownership is explicit

**Decision:** An adapter explicitly marks whether a captured package may be deleted by Core after durable storage.

**Reason:** Core must not guess whether an adapter-returned path is a temporary copy, a cache entry, or user-owned data. Cleanup authority must be part of the contract.

## D-023: Published revisions are immutable

**Decision:** Environment and state revision IDs may not be overwritten after publication. Only World head metadata is mutable.

**Reason:** Restore, Fork, recovery, history, and debugging all require revision identifiers to continue referring to the same historical content.

**Consequence:** The local backend rejects duplicate revision IDs. State metadata and payload are staged together and published only after both writes complete.

## D-024: Canonical heads advance last

**Decision:** A World's current revision pointer is updated only after the new immutable revision is durably stored.

**Reason:** A failed capture or storage write must leave the last known-good canonical World intact. An orphaned immutable revision is safer than a canonical head pointing at incomplete data.

## D-025: Product failures use typed exceptions at stable boundaries

**Decision:** Expected product failure categories such as missing Worlds, missing revisions, adapter mismatches, integrity problems, and session conflicts use dedicated exception types.

**Reason:** A future desktop UI must be able to map failure categories to recovery actions without parsing human-readable exception strings.

## D-026: Architecture boundaries are tested automatically

**Decision:** The test suite parses project references and enforces the intended dependency direction.

**Reason:** Documentation alone cannot prevent a future shortcut from making Core depend on Infrastructure or one adapter depend on another. CI should reject boundary violations automatically.
