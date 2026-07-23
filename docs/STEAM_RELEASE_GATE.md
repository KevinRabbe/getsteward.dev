# Steam Release Gate

Reviewed: **2026-07-22**

Status: **FINAL RELEASE GATE — INTENTIONALLY DEFERRED WHILE STEWARD IS IN DEVELOPMENT.**

## Decision

Steward development must not wait for a real Steward Steam AppID, Steam publisher API key, or production Web API ticket verification.

Those credentials are introduced only after the product owner decides Steward is otherwise good enough to publish.

Until then, the project continues through adapter completion, UI/UX completion, deployment hardening, installer/update work, security/recovery validation, and release-candidate testing using the already-implemented Steam integration boundary without weakening or bypassing it.

## Development rule

```text
Steward still in development
    -> keep building and testing every independent product boundary
    -> do not request production Steam publisher credentials
    -> do not add a fake production-auth bypass
    -> do not claim real Steam acceptance

product owner decides "good enough to publish"
    -> obtain/configure real Steward Steam AppID and publisher credentials
    -> execute final Steam-specific acceptance
    -> upload/release only after that succeeds
```

The absence of production Steam credentials is therefore **not a development blocker**.

## What may be completed before the gate

- Windows Desktop/tray product behavior;
- Factorio and Palworld adapter behavior that can be proven independently;
- exact-environment capture/Verify/Repair;
- local Start/Host behavior;
- sharing/invitations/access-management UX and contracts;
- immutable transfer and one-writer authority;
- deterministic recovery and failure injection;
- backend container, PostgreSQL, S3-compatible storage, HTTPS/deployment behavior;
- installer/update behavior;
- diagnostics/support workflow;
- security, backup/restore, long-session, and large-World validation;
- commercial UI/accessibility polish.

## What remains intentionally unproven until the gate

The final release proof is the genuine production identity/distribution boundary:

```text
real Windows Steward build launched through Steward's Steam AppID
-> SteamAPI.Init returns that AppID
-> real GetAuthTicketForWebApi ticket
-> deployed backend verifies the ticket using the real publisher credential
-> two independent Steward installations authenticate
-> PC A shares/continues a real Factorio World
-> PC B accepts access and continues A's canonical revision
-> PC B commits the next revision
-> PC A observes the new canonical head
```

The same final release phase then repeats the relevant shared handoff acceptance for the other release adapter.

## Non-negotiable constraints

Production Steam acceptance may not be replaced by:

- a hard-coded SteamID;
- a static development token;
- an authentication-disable switch;
- a fake publisher key;
- a mocked ticket presented as live evidence.

Such mechanisms may exist only inside isolated automated tests where they already model a specific boundary. They may never become a release path.

## Relationship to E4

E4's **code/CI composition** remains complete when the real remote runtime is structurally connected and tested through its production contracts.

The historical E4 live-Steam sequence remains valid as an acceptance specification, but execution of the Steam-specific portion is deferred to this final release gate rather than blocking ongoing product development.

Any non-Steam deployment or product defect discovered before release should still be fixed from concrete evidence as normal development work.
