# Portable World two-PC acceptance — Factorio V1

This procedure is the physical acceptance gate for the first Safe World portable-World vertical slice.

It validates the user promise:

> I spent hundreds of hours building this. Download it and explore it yourself.

The automated software composition is already qualified through the exact code head `7758705574899b749e3dbe2ab5852fa3f5149893`. This procedure covers the boundaries CI cannot prove: two physical Windows machines, Windows file activation, the real Factorio executable, and real save/load persistence across independent machines.

## Scope

Use:

- two separate Windows PCs;
- the same exact Safe World build on both PCs;
- the same exact Factorio version on both PCs;
- **vanilla Factorio with no user mods** for this first physical acceptance;
- one ordinary non-autosave Factorio World on PC A.

Do not add mods to this acceptance. Safe World currently verifies exact Factorio environments conservatively but does not automatically download/synchronize missing game versions or mods. Mod provisioning is a separate capability and must not obscure the portable-World result.

## Evidence to retain

Create one small evidence folder containing:

- the Safe World build/commit identity used on both PCs;
- the Factorio version observed on each PC;
- the exported `.safeworld` SHA-256 from PC A;
- the transferred `.safeworld` SHA-256 from PC B;
- the creator World ID from PC A;
- the viewer World ID from PC B;
- `pc-a-evidence.json` and `pc-b-evidence.json` from the read-only evidence probe when used;
- screenshots or notes showing the creator marker and viewer-only marker results;
- any Safe World incident ID or diagnostic log path if a step fails.

Do not include private tokens, Steam credentials, or unrelated game configuration in the evidence bundle.

## Read-only evidence probe

`tools/SharedWorlds.PortableWorldProbe` reduces manual transcription during the physical run. It is acceptance tooling only: it never launches Factorio, changes a World, imports a file, or declares the physical milestone complete.

Keep the `acceptance-build.json` produced by `tools/e4-build-desktop.ps1` beside the exact Safe World package used on each PC. The probe verifies every package file against that manifest and derives a stable package fingerprint, so PC A and PC B can prove that they used the same package bytes rather than merely the same displayed version.

The probe also:

- computes the whole `.safeworld` SHA-256;
- reads only the bounded portable manifest for evidence/routing metadata;
- requires the artifact to be Factorio V1 scope;
- asks the real Factorio adapter to verify the exact recorded environment on the current machine;
- fails this V1 run when any non-built-in Factorio mod is required;
- identifies the creator World from the portable snapshot/state-revision identity on PC A;
- identifies the independent viewer World from `StartedFrom` provenance on PC B;
- requires the viewer World to be local/private, owned by the current local user, and to have fresh World/state identities;
- requires exactly one persistent `SharedWorlds.Desktop` process when viewer evidence is collected.

The probe does **not** inspect or parse `state.bin`. The normal Safe World import remains the authoritative hostile-input/hash boundary for the state payload. The probe also cannot prove which Windows shell gesture the human used, whether the creator marker is visible in the real game, whether the viewer-only marker persisted, or whether that viewer-only marker stayed absent on PC A. Those observations remain mandatory below.

After PC A has completed A4, collect creator evidence:

```powershell
dotnet run --project tools/SharedWorlds.PortableWorldProbe -- --creator `
  --file "C:\SafeWorldAcceptance\500h-Megabase.safeworld" `
  --build-manifest "C:\SafeWorldAcceptance\package\acceptance-build.json" `
  --output "C:\SafeWorldAcceptance\evidence\pc-a-evidence.json"
```

Transfer the exact `.safeworld` plus `pc-a-evidence.json` to PC B. After B3/B4 has imported the file while the original Safe World process remains running, collect viewer evidence:

```powershell
dotnet run --project tools/SharedWorlds.PortableWorldProbe -- --viewer `
  --file "C:\SafeWorldAcceptance\500h-Megabase.safeworld" `
  --build-manifest "C:\SafeWorldAcceptance\package\acceptance-build.json" `
  --creator-evidence "C:\SafeWorldAcceptance\evidence\pc-a-evidence.json" `
  --output "C:\SafeWorldAcceptance\evidence\pc-b-evidence.json"
```

The viewer probe fails closed when the Safe World package fingerprint, desktop executable hash, commit, portable file hash, snapshot identity, or required Factorio version differs from PC A. A successful probe run therefore removes those facts from manual comparison, but it does not replace the remaining real-game observations.

## Test data

Before starting, choose two unique visible in-game map tags or other unmistakable vanilla Factorio markers:

```text
SAFEWORLD-CREATOR-<YYYYMMDD-HHMM>
SAFEWORLD-VIEWER-<YYYYMMDD-HHMM>
```

The first marker proves the viewer received the creator's committed state. The second proves the viewer's subsequent history is independent from the creator World.

---

## PC A — Creator

### A1. Qualify the local environment

1. Install/run the exact Safe World build selected for this acceptance.
2. Confirm Factorio is installed and launchable.
3. Record the exact Factorio version.
4. Confirm the World is vanilla: no user mods for this test.
5. Add/manage the source World in Safe World if it is not already managed.

**Pass:** Safe World shows the source World under Factorio and can Continue it normally.

### A2. Create a uniquely identifiable committed state

1. Click **Continue** for the source World.
2. In Factorio, add the creator marker:

   `SAFEWORLD-CREATOR-<timestamp>`

3. Save/exit Factorio normally.
4. Let Safe World finish its normal post-session capture/commit.
5. Re-open/Continue once if necessary to confirm the creator marker survived the committed Safe World revision.

**Pass:** the creator marker is present after a normal Safe World Continue cycle.

### A3. Record source identity

In Safe World's technical details, record:

- source World ID;
- exact game version;
- current state/revision identity if exposed by the build.

The source World ID is required later to prove the viewer received an independent World rather than the same canonical identity. The evidence probe records this identity automatically when used.

### A4. Export the portable World

1. Click **Share a Copy**.
2. Save the file as, for example:

   `500h-Megabase.safeworld`

3. Confirm the operation completes successfully.
4. Compute the whole-file SHA-256 in PowerShell, or run the creator evidence probe above:

```powershell
Get-FileHash .\500h-Megabase.safeworld -Algorithm SHA256
```

5. Record the hash.
6. Transfer the exact file to PC B using any ordinary transport such as USB, cloud drive, Discord, or another file-transfer mechanism.

**Pass:** one completed `.safeworld` exists and its PC-A SHA-256 is recorded.

---

## PC B — Viewer

### B1. Start from an independent machine

1. Install/run the same exact Safe World build used on PC A.
2. Install the same exact vanilla Factorio version used on PC A.
3. Record that version.
4. Keep Safe World **already running** before opening the downloaded file. This deliberately exercises the qualified secondary-process activation forwarding path.
5. Confirm there is only one normal Safe World desktop process before file activation.

**Pass:** PC B is independently provisioned with the same vanilla Factorio version and Safe World is already open.

### B2. Verify transfer integrity

In PowerShell, compute the downloaded file's SHA-256, or let the viewer evidence probe compare it directly with PC A:

```powershell
Get-FileHash .\500h-Megabase.safeworld -Algorithm SHA256
```

Compare it with PC A.

**Pass:** PC A and PC B SHA-256 values are identical.

**Fail immediately:** the hashes differ. Do not continue with a modified/corrupted transfer.

### B3. Open the downloaded World through Windows

Preferred physical path:

1. Double-click the `.safeworld` while Safe World is already running.
2. If Windows has not yet selected Safe World for the file type, use **Open with → Safe World**. Windows may legitimately keep the user's default-app choice under user control.
3. Observe Safe World handling the activation.
4. Confirm a second persistent Safe World desktop process does not remain running as another local writer.

Fallback path, if Windows shell selection is unavailable because of machine policy:

- Safe World → **More → Open World File…** → select the same file.

Record which path was used.

**Pass:** the file is accepted by the already-running Safe World instance and no independent second writer remains. The viewer evidence probe independently requires exactly one persistent Safe World desktop process at evidence-collection time.

### B4. Verify independent canonicalization

After import, verify:

- the World appears under **Factorio**;
- the name matches the creator World;
- the details show `Started from <World> by <Creator>` when creator attribution was included;
- the local viewer World ID is **different** from the PC-A creator World ID;
- the World is local/private rather than silently shared;
- PC B's local user is the local owner/member, not the source creator identity.

Record the viewer World ID. The viewer evidence probe checks the identity, privacy, ownership, and source-snapshot invariants automatically when used.

**Pass:** source attribution is preserved, but canonical identity and local ownership are new and independent.

### B5. Explore the creator state with the real game

1. Click **Continue** on the imported viewer World.
2. Allow Safe World to prepare/restore the exact recorded environment.
3. Confirm the real Factorio executable launches.
4. Open the World.
5. Find the creator marker:

   `SAFEWORLD-CREATOR-<timestamp>`

**Pass:** the creator marker exists in the real game on PC B.

**Fail:** Factorio launches a blank/wrong World, the marker is absent, or Safe World silently substitutes a different game version/environment.

### B6. Prove viewer persistence

1. While on PC B, add the viewer-only marker:

   `SAFEWORLD-VIEWER-<timestamp>`

2. Save/exit Factorio normally.
3. Let Safe World finish its normal capture/commit.
4. Click **Continue** again on the viewer World.
5. Confirm both markers are present:
   - creator marker;
   - viewer-only marker.

**Pass:** the viewer-only change survives a complete Safe World exit/capture/Continue cycle.

---

## PC A — Independence proof

Return to PC A after PC B has committed the viewer-only marker.

1. Continue the original creator World on PC A.
2. Confirm the creator marker is still present.
3. Confirm the viewer-only marker is **absent**.

**Pass:** PC B's independent history did not mutate or synchronize back into PC A's original World.

---

## Required pass criteria

The physical acceptance passes only when all of the following are true:

- both PCs used the same exact Safe World build;
- both PCs used the same exact vanilla Factorio version;
- transferred `.safeworld` SHA-256 matched exactly;
- Windows/open-file activation reached the existing Safe World instance, or the documented fallback was required only because of OS policy;
- no second persistent Safe World local writer remained after activation;
- PC B created a fresh World ID rather than reusing PC A's World ID;
- source creator remained attribution, not viewer authority;
- the real Factorio executable loaded the creator's committed state;
- the creator marker was present on PC B;
- the viewer-only marker survived a second PC-B Continue cycle;
- the viewer-only marker never appeared in the original PC-A World;
- no silent game-version downgrade/upgrade or environment substitution occurred;
- no partial viewer World was left behind by any failed import attempt during the test.

## Automatic failure conditions

Stop and record the evidence if any of these occur:

- transferred file hashes differ;
- the imported World reuses the creator World ID;
- the source creator becomes the local viewer owner/member solely from portable metadata;
- Factorio opens the wrong or blank save;
- the creator marker is missing on PC B;
- the viewer marker disappears after a normal Save/Exit/Continue cycle;
- the viewer marker appears in PC A's original World;
- Safe World silently ignores an exact-version mismatch;
- a second Safe World process remains as an independent local writer;
- a failed import leaves a half-visible World that appears usable;
- the test unexpectedly depends on automatic mod downloading or mod synchronization.

## Known limitation after a successful V1 acceptance

A successful run proves the first vanilla Factorio portable-World vertical slice across two physical PCs.

It does **not** prove clean-machine automatic provisioning for modded Factorio Worlds. Safe World currently fails conservatively when the exact required environment cannot be reproduced; automatic game/mod download and synchronization remain a separate future qualification target.

## Result record

Record the final result in this form:

```text
Date:
Safe World build / commit:
PC A Factorio version:
PC B Factorio version:
PC A World ID:
PC B World ID:
.safeworld SHA-256 on PC A:
.safeworld SHA-256 on PC B:
Open path used on PC B: Windows double-click / Open With / in-app fallback
Creator marker present on PC B: PASS / FAIL
Viewer marker persisted on PC B: PASS / FAIL
Viewer marker absent on PC A: PASS / FAIL
Single Safe World writer after activation: PASS / FAIL
Overall: PASS / FAIL
Notes / incident IDs:
```

Do not mark the creator→viewer physical milestone complete until this record is PASS.
