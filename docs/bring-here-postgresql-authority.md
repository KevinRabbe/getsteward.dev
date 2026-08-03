# Bring Here PostgreSQL authority

This slice persists owned installations and private World-location claims without creating synchronization or last-write-wins behavior.

## Tables

- `steward_owned_installations`: opaque installation identity permanently bound to one authenticated owner.
- `steward_owned_world_locations`: one exact state/environment head per owner, World and installation.

The composite foreign key requires the installation and claim to name the same owner.

## Mutation rule

Location changes and removal use exact state/environment compare-and-swap. `observed_at` is presentation/ordering metadata only and cannot authorize replacement of a divergent head.

The first-publication primary key prevents duplicate claims. A dedicated transaction-serialization regression remains required before this slice is frozen so concurrent absent-row publication returns deterministic Created/Conflict outcomes rather than surfacing a database uniqueness race.
