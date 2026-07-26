# V2 Valheim 1.0 gate

Status: **DEFER SAVE-REPRESENTATION IMPLEMENTATION UNTIL 1.0**

Rechecked: **2026-07-26**

Valheim remains one of the four primary V2 Friends Build games, but Steward should not implement a pre-1.0 World-storage model that is already known to be changing.

## What is stable enough to rely on

Iron Gate's current dedicated-server contract still gives Steward the native mechanisms it needs later:

```text
Valheim Dedicated Server
-> -world <name> creates/loads the native World
-> -savedir <path> isolates server-owned save state
-> "Game server connected" is documented readiness evidence
-> clean server stop uses CTRL+C
```

The server also supports two networking modes:

- Steam backend: configured port plus port+1; Internet hosting normally requires reachable forwarded ports.
- Crossplay backend: `-crossplay` uses a relay and does not require router port forwarding.

Those are server/runtime facts. They do not require Steward to parse or synthesize World save bytes.

## Why state implementation is frozen now

The current default Valheim branch still predates the new save representation, but Iron Gate's current Public Test `0.221.13` explicitly replaces the historical two-file World representation with a chunked folder-based system:

```text
old/current default
<world>.db
<world>.fwl

public test / intended new system
World stored in folders
.db data split into multiple chunk files
backups stored in separate folders
```

Iron Gate is actively requesting save-data and networking testing of this system before 1.0.

Valheim 1.0 is scheduled for **2026-09-09**. Iron Gate also states that existing saves remain usable after the 1.0 update, so Steward does not need a migration parser merely to preserve old Worlds.

Therefore:

> **Do not build a Steward Valheim adapter whose canonical assumption is “a World is one `.db` + one `.fwl` pair.”**

That would encode a representation already known to be replaced.

## Intended Steward boundary after 1.0

Recheck the released 1.0 build first, then implement the smallest native-state boundary that matches what the game actually ships.

Prefer opaque ownership over save parsing:

```text
native Valheim World state
-> adapter identifies the current World-owned native files/directories
-> Steward captures those bytes opaquely
-> restore reproduces the same native layout in an adapter-owned -savedir
-> Valheim itself interprets/generates the format
```

Steward should not decode chunk contents, synthesize native save structures, or invent a compatibility layer unless a measured product need later requires it.

## Exact re-check gate

After Valheim 1.0 releases on 2026-09-09, establish with the released Steam client + Dedicated Server tool:

1. the exact live World-owned directory/file layout created under an isolated `-savedir`;
2. which paths are current authoritative World state versus native backup history;
3. whether an existing pre-1.0 World is converted in-place or materialized into a new layout when loaded;
4. whether a World captured from the released client can be restored into the released dedicated server without extra machine-local state;
5. whether dedicated-server build identity still needs to match the client for the intended Friends Build flow;
6. the clean save/stop boundary required before capture;
7. the smallest Join path for the chosen backend, preferring the documented crossplay relay if it removes a real port-forwarding problem for the friend group.

Only then should Valheim gain production adapter state/Host/Join capability claims.

## Explicitly not doing now

- no `.db` parser;
- no `.fwl` parser;
- no pre-1.0 pair-only portable package;
- no guessed future chunk-folder naming;
- no custom World migration code;
- no NAT traversal implementation merely because networking has multiple modes;
- no permanent Steward Server object.

The uncertainty removed by this checkpoint is itself the result: **the old save representation is not a stable target, while the native dedicated-server control surface remains useful.**

## Evidence

Current evidence reviewed on 2026-07-26:

- Iron Gate — Valheim 1.0 FAQ (1.0 date and existing-save compatibility)
- Iron Gate — A Guide to Dedicated Servers (`-world`, `-savedir`, readiness, shutdown, Steam ports, crossplay relay)
- Iron Gate / Steam announcement — Patch 0.221.13 Public Test (new chunked folder save system)
- Iron Gate — May 2026 developer update requesting testing of the new save-data/network behavior
