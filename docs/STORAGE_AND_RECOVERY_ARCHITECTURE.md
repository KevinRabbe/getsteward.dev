# SafeWorld storage and recovery architecture

This document defines the storage boundary that SafeWorld code must converge on. It exists to prevent product-name path migrations or game-specific fixes from becoming the architecture.

## Core rule

A filesystem path is a runtime location, not durable identity.

SafeWorld may use absolute paths while a process is running, but durable authority/recovery records must be reconstructable after an application upgrade, root migration, reinstall of a game to another library, or other machine-local path change whenever the underlying state still exists.

## State classes

SafeWorld has four materially different storage classes. They must not share lifecycle assumptions.

### 1. Durable SafeWorld authority

Examples:

- canonical World metadata and immutable revisions
- authority/generation metadata
- membership
- recovery journals
- device settings and credentials owned by SafeWorld

Properties:

- one resolved application root per user installation
- migration is explicit, versioned, fail-closed, and completed before writers start
- no game adapter, CLI command, infrastructure store, or peer component may independently derive an application root from `LocalApplicationData`
- old builds must fail loudly rather than recreate a second authority root

### 2. Recoverable execution state

Examples:

- isolated prepared World workspaces
- server-side writable state that has not yet been captured and committed

Properties:

- may need to survive a crash
- is not canonical authority
- is tied to a stable `WorkspaceId` and exact World/environment/base-state identity
- durable recovery does not treat an absolute path as identity
- SafeWorld-managed workspaces are resolved from the current storage layout plus `WorkspaceId`
- native game workspaces are reconstructed from adapter-owned stable facts plus the current installation/environment

### 3. Disposable staging

Examples:

- captured state packages before durable storage
- materialized immutable revision packages used only for restore
- transfer buffers
- extraction/copy staging that is transaction-local

Properties:

- disposable by contract
- never authoritative
- never part of durable-root migration
- normally uses the system temporary tree
- failure to clean it up is a hygiene problem, not a recovery/authority event

`CapturedState.DeletePackageAfterStore = true` is the existing contract for this category.

### 4. Game/player-owned native state

Examples:

- a human player's Factorio profile, settings, achievements, blueprint library, and account data
- a game-owned dedicated-server save tree when the validated hosting model requires it
- game installation and launcher data

Properties:

- SafeWorld does not rename or absorb it into the SafeWorld application root merely for uniformity
- adapters may discover native locations using game/platform rules
- human profile state remains game-native unless isolation is fundamentally required
- native prepared World locations must still have a stable recovery descriptor; the raw pathname is not the journal identity

## Required invariants

1. One World has one canonical authority state.
2. One SafeWorld process has one resolved durable application root.
3. Only the application-root authority knows current/historical SafeWorld product directory names.
4. Game adapters do not invent SafeWorld application-level persistence roots.
5. Human player profiles remain native unless the game fundamentally requires isolation.
6. Portable World state never depends on machine-specific absolute paths.
7. Crash recovery survives application-root migration when the recoverable state still exists.
8. Disposable staging is explicitly disposable and never promoted into authority by directory placement.
9. Anything not provably disposable is preserved on ambiguity.
10. Old versions fail loudly instead of silently creating a second authority store.
11. Game adapters implement game policy; SafeWorld/platform code implements application storage architecture.
12. Cleanup may delete only state whose ownership has been positively proven.

## Prepared World model

`PreparedWorld.WorkingDirectory` may remain an absolute runtime path. The error is persisting that value as durable identity.

The durable recovery model must instead carry a location descriptor with two first-class forms:

### SafeWorld-managed workspace

Identity is the recovery record's `WorkspaceId` plus adapter identity. The current process resolves the corresponding directory from the configured managed-workspace root.

A new managed workspace must therefore receive its `WorkspaceId` before the adapter creates files. The current lifecycle is inverted because it asks the adapter to choose a path first and creates the durable `WorkspaceId` afterward.

### Native game workspace

Identity is adapter-defined stable data sufficient to reconstruct the current native location from the selected `GameInstallation` and exact `EnvironmentRevision`.

For example, Palworld's validated dedicated-host path can reconstruct its World directory from the dedicated-server installation metadata and the dedicated World id already carried by its environment. Persisting the resulting absolute path adds fragility without adding identity.

## Target preparation boundary

Preparation must converge on this direction:

1. Core loads the exact World/environment/base state.
2. Core allocates `WorkspaceId` before writable preparation.
3. The platform storage layout supplies the SafeWorld-managed workspace location for that id.
4. The adapter receives a preparation context containing the stable workspace identity and managed location it may use when isolation is appropriate.
5. The adapter either:
   - materializes into the supplied managed workspace; or
   - deliberately selects a validated native game location.
6. The returned prepared World includes a durable recovery descriptor describing which case was chosen.
7. Core journals that descriptor before writable session launch.
8. Recovery resolves the descriptor into a current runtime `PreparedWorld`; UI and recovery services never call `Directory.Exists` on a pathname taken directly from the journal.

## Recovery journal evolution

A versioned recovery record is required. New records must not use `WorkingDirectory` as truth.

Legacy records need explicit classification during migration:

- recognized legacy SafeWorld/SharedWorlds managed workspace: rebase only when the old/new ownership relationship is provable
- recognized `Steward/workspaces/<adapter>/<id>` workspace: preserve and convert to the managed-workspace descriptor without deleting evidence
- validated native-game workspace: convert using adapter/environment facts when reconstructable
- unknown/ambiguous absolute path: retain the legacy record and fail closed; never guess, move, merge, or delete it

Compatibility code for legacy absolute paths is migration code, not the permanent runtime model.

## Application storage layout

The final application-owned layout is composed once from the resolved SafeWorld root. Components receive the subroot they need; they do not call `Environment.GetFolderPath(LocalApplicationData)` to rediscover it.

Conceptual durable layout:

```text
<SafeWorld>/
  data/                 canonical World authority
  settings/             device-owned settings/credentials
  recovery/             durable recovery journals if not colocated with data
  workspaces/           recoverable SafeWorld-managed execution state
  remote/               legacy/compatibility state while still required
  logs/                 durable diagnostics
```

Disposable capture/materialization/transfer staging belongs under the system temporary tree, not this durable layout.

## Migration rules

- never merge two plausible authority roots
- never copy and then continue with two writable roots
- never follow reparse points when proving SafeWorld-owned migration input
- never create a compatibility junction/symlink merely to hide stale path construction
- never rewrite an arbitrary absolute recovery path by string prefix alone
- migrate identity and ownership, not just directory spelling
- preserve unresolved evidence and surface a recoverable error rather than deleting or silently starting fresh

## Refactor sequence

1. **Disposable staging:** move adapter capture-package output and Core materialization scratch to the temporary staging facility.
2. **Single application-root composition:** remove default/independent SafeWorld roots from Infrastructure and CLI; only the root authority resolves product storage.
3. **Recovery identity v2:** introduce prepared-world recovery descriptors and a resolver; stop new journals from persisting absolute paths.
4. **Managed workspace allocation:** allocate `WorkspaceId` before preparation and move SafeWorld-owned adapter workspaces behind the common managed-workspace facility.
5. **Legacy recovery migration:** classify and convert existing SharedWorlds/Steward/native records fail-closed.
6. **Game profile audit:** for each human graphical launch, explicitly classify World state versus native player profile. Factorio human processes use the native Factorio/Steam profile while dedicated authority remains isolated.
7. **Remove compatibility code:** only after migration tests prove no durable state depends on old pathname semantics.

Each slice must be enforced by source/behavioral tests that express the invariant, not by a list of files that happened to be wrong during one audit.
