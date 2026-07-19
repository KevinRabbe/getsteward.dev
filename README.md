# SharedWorlds

> Steam is the platform. Games are adapters. Worlds are the product.

This repository is the first durable foundation for the Shared Worlds concept. The repository name is temporary and can change later without affecting the architecture.

## Architecture rule

The Core must never contain game-specific branches such as `if (game == Factorio)`.

- **Core** knows *what* must happen: World lifecycle, revisions, sessions, storage boundaries.
- **Game adapters** know *how* a specific game makes it happen.
- **Adapters may use any ecosystem internally**: Steam, CurseForge, Modrinth, Prism, custom launchers, filesystem layouts, etc.
- **Storage and live coordination are generic boundaries**; Steam-backed implementations come later.

## Initial adapters

1. Factorio
2. 7 Days to Die
3. Project Zomboid

## First vertical slice

1. Implement installation discovery for the three adapters.
2. Display only installed supported games.
3. Implement Factorio save discovery.
4. Import one Factorio save as a World.
5. Capture an EnvironmentManifest.
6. Launch the World locally.
7. Detect process exit.
8. Capture a new StateRevision.

No networking is required for this slice. Everything in it is intended to remain part of the final architecture.

## Temporary environment fingerprint

`EnvironmentFingerprint.Compute()` hashes only the adapter-produced canonical manifest. The manifest is authoritative; the hash is a disposable comparison aid and must not be treated as proof that every file on disk is intact.
