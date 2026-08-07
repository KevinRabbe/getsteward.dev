# Closed-alpha live deployment request

Status: **pre-execution boundary; does not provision, deploy, or authorize publication.**

The first real E4-A acceptance topology deliberately has one public API hostname and one Linux `amd64` host. That same hostname is the SSH target. Caddy is the only public HTTPS proxy and forwards to `127.0.0.1:8080`; backend port `8080` must not be public.

A finalized closed-alpha candidate contains `prepare-closed-alpha-live-deployment.ps1`. Run that copy from inside the verified candidate to bind immutable release identity to the intended live-host coordinates:

```powershell
pwsh ./prepare-closed-alpha-live-deployment.ps1 `
  -BundleDirectory . `
  -ExpectedPublicIpv4 <globally-routable-ipv4> `
  -SshHostKeySha256 SHA256:<pinned-host-key-fingerprint> `
  -OutputPath ../live-deployment-request.json
```

The request is intentionally non-secret. It records the exact release version, commit, API URL, backend image identity and TAR hash; API/SSH hostname; expected public IPv4; SSH port/user/host-key fingerprint; fixed remote paths; required host tools; one-proxy network contract; and unchanged publication state.

It does **not** resolve DNS, contact SSH, read `/etc/steward/backend.env`, transfer credentials, run the backend, modify Caddy, or probe public HTTPS. Those are execution-time facts and must be proven against the real host rather than inferred during planning.

The protected backend environment stays on the live host at `/etc/steward/backend.env`, root-owned mode `0600`. The byte-manifested deployment planner is then run on that host with `-RequireDeployable`, so PostgreSQL `VerifyFull`, private S3-compatible storage credentials, Friends Build identities, exact proxy trust, cleanup bounds, and the remaining backend configuration are validated beside the secret file without copying those values into request evidence.

This request is not publish authorization. Physical PC A -> PC B -> PC A Bring Here evidence remains a separate release-manifest gate.
