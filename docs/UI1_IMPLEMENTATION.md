# UI-1 Implementation Checkpoint

UI-1 replaces transitional desktop behavior with the approved first-release shell/lifecycle contract without inventing backend authority that does not exist yet.

## Current status

Status: **implementation requirements complete; Windows build/test confirmation pending**.

Implemented in the current UI-1 slice:

- Steward has a persistent tray icon whenever the user-session desktop process runs;
- closing the main window hides it instead of quitting the process;
- tray exposes `Open Steward` and guarded `Quit Steward`;
- runtime lifecycle phases feed one conservative responsibility tracker used by tray/Quit/update policy;
- unresolved lifecycle/recovery responsibility blocks ordinary Quit;
- tray status reflects approved concepts such as Running, Saving World, Recovery needed, and Action required;
- startup loads durable workspace-recovery records before the unified game/World surface initializes;
- a recovery record found after restart cannot be silently presented as normal Ready state;
- the generic runtime also hard-blocks new writable Start/Host before coordinator acquisition while durable responsibility remains;
- selected-World presentation surfaces `Preparing`, `Running`, `Saving World`, `Recovery needed`, or `Action required` directly from the runtime responsibility tracker;
- Start World / Host World are disabled whenever this desktop has active or unresolved writable responsibility, including when that responsibility belongs to another World;
- temporary `Host World` no longer depends on persistent Steward sharing in Core or UI action gating;
- Host availability is driven by adapter host capability plus the local device hosting preference;
- approved `Start World` / `Host World` terminology replaces the transitional Continue/Host labels;
- `Only on this PC` replaces the transitional `Local only` wording;
- the desktop/window branding says Steward with the product-facing `Click World. Play.` shell line;
- technical World/environment/state revision identifiers live behind a collapsed `Technical details` expander;
- wide layout keeps World navigation beside selected-World details;
- narrow layout uses the approved focused master-detail behavior with `Back to Worlds`;
- the window minimum width allows the narrow layout to be reached;
- the old Factorio-only desktop execution path and its XAML event-handler contract have been removed;
- the duplicate compact Import pipeline, hidden XAML anchors, and detach-compatibility hook have been deleted;
- the game-first Import workspace is the single Import product surface;
- game tiles append the same runtime attention state used by the tray/World details when one of their Worlds is Preparing, Running, Saving World, Recovery needed, or Action required;
- `Share World` no longer flips a local enum and falsely claims persistent remote sharing;
- until BE-2/UI-4 connects real remote World authority/access, Share/Manage access remains an honest entry point that leaves canonical local state unchanged;
- WinForms is used only for `NotifyIcon`; its namespace is kept explicit so existing WPF `Application`/`MessageBox` usage is not polluted by implicit WinForms imports;
- temporary template-helper abstractions introduced during UI refactoring were removed again in favor of direct WPF construction.

## Truthfulness rule

`Shared` means a real Steward backend/access relationship exists. A local metadata mutation is not sufficient.

Therefore the desktop must not present a World as Shared until the backend flow has actually:

1. authenticated the caller;
2. registered shared World authority;
3. uploaded and verified the initial canonical package/environment as required;
4. committed the shared metadata/head transaction;
5. established Access Manager membership;
6. created World-access invitations as requested.

This keeps UI-D003/UI-D009 aligned with BE-D002/003 rather than allowing the desktop to manufacture a state the backend cannot support.

## Remaining UI-1 gate

The implementation is not green until the Windows desktop project compiles and the repository tests execute successfully under the repository's warnings-as-errors/nullability configuration.

Direct WPF interaction automation may be expanded later where it adds reliable coverage, but the safety policy itself already has deterministic Core/runtime tests. Lack of ornamental UI automation does not justify inventing a second test-only product model.

## Boundary

UI-1 does not implement:

- Steam authentication;
- remote shared World registration;
- World-access invitation persistence;
- Access Manager backend mutations;
- Join before a validated host/capability exists;
- game-name routing inside Core;
- permanent game hosting;
- branch/merge/conflict UI.

Those remain in their numbered backend/runtime/UI milestones.
