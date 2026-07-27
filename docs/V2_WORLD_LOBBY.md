# World Lobby

Status: **CURRENT PRODUCT CONTRACT — SMALL OPERATIONAL LOBBY, NOT A SOCIAL NETWORK**

## Product rule

The World itself is the lobby.

Steward does not add a separate party, friends graph, chat, voice, matchmaking, public discovery, or social-network layer.

The first-release lobby answers exactly three operational questions:

1. Who belongs to this World?
2. Who is currently playing this World through a Steward-observed session?
3. Who is the current Host?

Canonical rule:

> **World Lobby = World membership + ephemeral current-player presence + authoritative current Host.**

Steam and Discord remain the social surfaces around the World. Steward owns only the World-specific operational facts that neither one can derive from Steward's shared-World lifecycle by itself.

## Authority reused

The lobby deliberately composes existing truths rather than creating a Lobby/Party domain object.

### World group

The canonical World access list remains membership authority.

- active members appear in the World group;
- the existing Access Manager is labeled as the administrative access responsibility;
- revocation-pending membership remains an access-state fact rather than a gameplay role;
- membership mutations continue through the existing access/invitation API.

### Current Host

Host truth comes only from the existing reservation-backed Host-presence path.

The lobby may show the Host identity and whether the managed Host is starting/ready, but it does not create another Host flag or infer Host from player presence.

Lobby presentation never receives reservation session/generation/installation identity, Join tokens, or Host network coordinates merely to label the Host.

### Playing now

Non-Host player presence is deliberately short-lived presentation evidence.

Current first-release rule:

```text
automatic Join launches a real client process
-> Steward publishes current authenticated member presence
-> refresh every 15 seconds while Steward observes the client session
-> client session ends
-> Steward clears presence

backend visibility TTL = 45 seconds
```

Only active World members may publish/read presence. Revocation-pending identities are not rendered as currently playing.

If explicit cleanup cannot reach the backend, TTL expiry removes stale presentation state.

Manual/native Join that Steward does not observe does not fake presence. A missing presence row is therefore not authority evidence that a player is definitely absent from the native game session.

## Desktop presentation

The selected shared-World details surface contains a small read-only lobby card:

```text
WORLD LOBBY

Playing now
Kevin — HOST
Alex

World group
Kevin — Access Manager
Alex
Sarah
```

The existing **Manage access** dialog remains the mutation surface. The lobby card does not duplicate invitation/removal/transfer workflows.

For the private Friends Build, display names come from the already bounded configured identity roster. Stable provider/external IDs remain persistence identity and are not replaced by display names.

Production Steam friend/invite/name presentation remains a separate release-completeness item; Steward must reuse Steam rather than build a friends graph.

## Presence is non-authoritative

Player presence may never:

- acquire, release, reclaim, or extend writable authority;
- change membership;
- grant Join permission;
- decide who is Host;
- advance or select canonical World state;
- clear recovery responsibility;
- become durable presence history.

A presence failure is a presentation failure. It must not terminate an already-running read-only Join session.

## Deliberately absent

The World Lobby does **not** add:

- global online/offline presence;
- gameplay presence outside the selected World;
- friends or follower relationships;
- chat or voice;
- parties;
- social roles;
- profiles/feed/activity history;
- public lobby/server discovery;
- matchmaking.

Discord remains the communication/social surface. Steam remains the platform identity/friends/invitation surface where useful.
