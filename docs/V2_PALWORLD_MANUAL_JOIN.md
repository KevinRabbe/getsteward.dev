# V2 Palworld manual Join boundary

## Product behavior

Palworld's current V2 Friends Build path deliberately stops at the native dedicated-server boundary:

```text
Steward Host
-> managed Palworld dedicated server becomes ready
-> Steward publishes the host-observed IPv4 + game port 8211
-> friend sees the exact IP:port in Steward
-> friend opens Palworld and enters that endpoint in Join Multiplayer
```

Steward does **not** claim a one-click Palworld Join path. The adapter does not advertise `AutomaticClientJoin`, and `WorldJoinService` remains automatic-only.

The manual surface is intentionally smaller than the old removed guided-manual Join design. `IManualDirectConnectProvider` is presentation-only: it turns already-ready `HostConnection` evidence into native direct-connect guidance. It does not prepare a client workspace, launch Palworld, observe the client's lifetime, or own cleanup.

## Host endpoint

The currently qualified managed Palworld host path does not override Palworld's dedicated-server listen port. Steward therefore publishes the documented default game port `8211` only after the managed host session has started and been registered.

REST/admin control remains private to the host machine. Steward does not publish the REST port or transient admin password as Join material.

## Explicit empirical boundary

CI can prove the composition and presentation, but it does **not** prove that a friend on another real network can reach the endpoint.

Required real test:

```text
PC A Host Palworld World
-> backend presence becomes Starting then Ready
-> Ready contains host-observed IPv4 + UDP 8211
-> PC B sees the same IP:8211 in Steward
-> PC B enters it in Palworld Join Multiplayer
-> PC B reaches the exact managed dedicated server
-> PC A stops/saves through Steward
-> presence clears before capture/commit
```

Record whether Windows Firewall/router configuration was already present and whether failure occurs before Palworld can reach/authenticate to the server.

Do not add `steam://connect`, keyboard/mouse UI automation, public-IP lookup, forwarded-header trust, UPnP, STUN, relay, or generic NAT traversal until that two-network test demonstrates a concrete missing mechanism.
