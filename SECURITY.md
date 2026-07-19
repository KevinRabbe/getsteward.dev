# Security Policy

This product will eventually handle local game data, remote World state, user identity, and session coordination. Security issues that could expose credentials, private tokens, user files, or enable unintended filesystem access should not be disclosed in a public issue before remediation.

## Reporting a vulnerability

Prefer GitHub private vulnerability reporting or a private GitHub Security Advisory for this repository when available. If those channels are not enabled, contact the repository owner privately through GitHub before publishing technical details.

Include:

- affected version or commit
- affected operating system
- reproduction steps
- expected and actual behavior
- potential impact
- whether real user data was accessed or modified

Do not include real credentials, authentication tokens, private join tokens, or private save data in a report.

## Security-sensitive areas

Extra review is required for changes involving:

- filesystem deletion or recursive copy
- archive extraction
- externally supplied paths
- remote storage identifiers
- authentication or identity providers
- Steam or other platform tokens
- lobby/session join tokens
- automatic mod/package download and extraction
- launching external processes or constructing command-line arguments

## Supported versions

The project is pre-release. Security fixes currently target the active development line rather than maintaining multiple released branches.
