# Palworld adapter plan

## Why Palworld is next

Palworld moves ahead of Valheim in the implementation order because it gives SharedWorlds an immediate real-world multi-device test group:

- three active players
- three separate PCs
- an existing shared World
- two players already familiar with SaveSync

The goal is not to prove that Palworld is a better game or a cleaner adapter target. The goal is to maximize test leverage and move from a single-machine technical proof to a real group product proof.

North Star for this slice:

> Three people, three PCs, one World, no save juggling.

## Safety rule

The first Palworld experiments must use a copied test World.

Do not mutate or migrate the group's live Palworld / SaveSync World until the adapter has proven that it can preserve the complete World and player identity state through a dedicated-server round trip.

## Adapter boundary

Core should not learn Palworld-specific concepts.

The Palworld adapter owns:

- Steam client discovery
- Palworld Dedicated Server discovery
- Palworld save layout
- server configuration
- exact environment inspection
- server startup and readiness
- REST API save / shutdown control
- client connection details
- capture and restore of the authoritative World state

Core continues to own only the generic World lifecycle and revision semantics.

## Official dedicated-server primitives

Current official Palworld server documentation describes:

- a separate Palworld Dedicated Server application
- SteamCMD app ID `2394010` for the dedicated server
- `PalServer.exe` on Windows
- a configurable listening port, defaulting to `8211`
- private dedicated servers that players join by IP address and port
- `RESTAPIEnabled` and `RESTAPIPort` server settings
- REST endpoints for saving the World and shutting down the server

These primitives are a good fit for the same high-level lifecycle SharedWorlds proved with Factorio:

```text
restore canonical World state
-> start authoritative dedicated server
-> prove server ready
-> players join
-> play
-> request authoritative save
-> shut server down cleanly
-> capture authoritative state
-> commit new StateRevision
```

Palworld-specific mechanics stay inside the adapter.

## Important unknown: automatic client Join

The official connection guide currently documents entering an IP address and port in the in-game server list.

No supported client command-line auto-connect mechanism has been proven yet.

Therefore:

- do not mark `AutomaticClientJoin` as supported yet
- do not add brittle mouse / keyboard automation
- first prove dedicated-server hosting and safe World capture
- investigate a supported automatic Join mechanism separately

A temporary manual Join step is acceptable during adapter validation. The final product target remains one-click Join.

## First implementation sequence

1. Discover the installed Palworld Steam client.
2. Discover the optional Palworld Dedicated Server installation independently.
3. Inspect the real three-player setup without modifying it.
4. Copy the World into a disposable test location.
5. Determine the complete authoritative save boundary, including player identity data.
6. Prove copied-World -> dedicated-server -> save -> shutdown -> copied-World round trip.
7. Build an immutable Palworld `EnvironmentManifest` from the minimum exact information required to reproduce the World.
8. Move server runtime into an adapter-owned workspace.
9. Use the REST API for readiness/control where appropriate, especially save and shutdown.
10. Add real multi-PC Join and remote state synchronization.
11. Test host changes among the three players.
12. Compare the repeated real workflow directly against SaveSync.

## Product proof

Factorio proved:

> SharedWorlds can own an authoritative server lifecycle correctly.

Palworld should prove:

> A real friend group can continue the same World across multiple PCs without thinking about save ownership or who hosted last time.

## Infrastructure rule

This work must not depend on a SharedWorlds-operated fleet of permanent Palworld servers.

The intended model remains:

```text
World persists
-> host is temporary
-> authoritative server runs only when somebody is playing
```

Optional paid infrastructure may exist later for services that genuinely cost recurring money, such as managed backup or relaying, but the Palworld World lifecycle itself must remain usable without a mandatory subscription.
