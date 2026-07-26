# V2 World Lobby

## Product rule

The World itself is the lobby.

Steward does not add a separate party, group, friends graph, chat, voice, matchmaking, or presence system merely to show who shares a World.

The lobby answers one product question:

> Who is part of this World?

## Existing authority reused

The canonical World access list remains the membership authority.

The backend continues to store stable provider/external identity references for World membership and invitations. Friends Build display names are presentation data from the already bounded private identity configuration; they are not copied into World membership persistence.

For the private Friends Build, an authenticated `friends-build` session may read the configured public identity roster:

- provider;
- stable opaque external ID;
- display name.

Credential hashes and bootstrap credentials are never part of this response.

## Desktop behavior

For Friends Build shared Worlds:

- the existing access dialog is presented as the World lobby;
- members are shown by display name;
- the Access Manager is marked only as the existing operational access responsibility;
- inviting uses a dropdown of configured friend names rather than requiring an opaque ID;
- current members and the caller are removed from the invite choices;
- remove access, Access Manager transfer, leave, accept, and decline continue to use the existing access API and stable identity references.

Steam mode keeps its existing SteamID64 invitation path for the later public/commercial release.

## Deliberately absent

V2 does not add:

- online/offline presence;
- gameplay presence;
- host-status polling for the lobby;
- chat or voice;
- parties;
- social roles;
- a friends graph;
- public discovery or matchmaking.

Those are not needed to make World membership visible and easy to use with Discord as the communication layer.
