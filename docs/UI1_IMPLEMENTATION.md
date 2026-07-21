# UI-1 Implementation Checkpoint

UI-1 replaces transitional desktop behavior with the approved first-release shell/lifecycle contract without inventing backend authority that does not exist yet.

## Current status

Status: **in progress — background/tray lifetime and action semantics integrated; shell/navigation cleanup and real shared-access integration remain**.

Implemented in the current UI-1 slice:

- Steward has a persistent tray icon whenever the user-session desktop process runs;
- closing the main window hides it instead of quitting the process;
- tray exposes `Open Steward` and guarded `Quit Steward`;
- runtime lifecycle phases feed one conservative responsibility tracker used by tray/Quit/update policy;
- unresolved lifecycle/recovery responsibility blocks ordinary Quit;
- tray status reflects approved concepts such as Running, Saving World, Recovery needed, and Action required;
- startup loads durable workspace-recovery records before the unified game/World surface initializes;
- a recovery record found after restart cannot be silently presented as normal Ready state;
- temporary `Host World` no longer depends on persistent Steward sharing in Core, legacy desktop action gating, or the unified action layer;
- Host availability is driven by adapter host capability plus the local device hosting preference;
- unified Host tooltips/text no longer instruct the user to Share first;
- `Share World` no longer flips a local enum and falsely claims persistent remote sharing;
- until BE-2/UI-4 connects real remote World authority/access, Share/Manage access remains an honest entry point that leaves canonical local state unchanged;
- WinForms is used only for `NotifyIcon`; its namespace is kept explicit so existing WPF `Application`/`MessageBox` usage is not polluted by implicit WinForms imports.

## Truthfulness rule

`Shared` means a real Steward backend/access relationship exists. A local metadata mutation is not sufficient.

Therefore the transitional desktop must not present a World as Shared until the backend flow has actually:

1. authenticated the caller;
2. registered shared World authority;
3. uploaded and verified the initial canonical package/environment as required;
4. committed the shared metadata/head transaction;
5. established Access Manager membership;
6. created World-access invitations as requested.

This keeps UI-D003/UI-D009 aligned with BE-D002/003 rather than allowing the desktop to manufacture a state the backend cannot support.

## Remaining UI-1 work

- remove or quarantine obsolete legacy Factorio-only action composition now that unified startup owns the active desktop path;
- finish the intended global shell/navigation replacement instead of layering more dynamic UI over the transitional XAML;
- preserve Games -> Worlds hierarchy and responsive selected-World details while removing duplicate legacy controls/handlers;
- expose lifecycle responsibility/attention in the unified World presentation without inventing remote states;
- keep `Share World` / `Manage access` non-destructive until BE-2/UI-4 provides the real access flow;
- add automated desktop-level tests where practical for close-to-tray and safe-Quit policy;
- build/test confirmation on Windows under warnings-as-errors.

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
