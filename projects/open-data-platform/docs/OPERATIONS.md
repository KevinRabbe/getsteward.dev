# Open Data Platform Operations Runbook

This runbook covers the local Windows deployment of the GLEIF pipeline, read-only API, backup replica, and failover restore.

## Daily operation

1. Run `scripts/monitor_gleif.ps1` from the project directory.
2. Treat exit code `0` as healthy and exit code `3` as an operational alert.
3. Inspect the JSON fields `alerts`, `deployment.checks`, `pipeline_lock`, and `recovery` before retrying anything.
4. Review `python .\odp.py events --limit 50` when a stage fails.

The monitor is report-only. It does not delete temporary files, remove locks, or change retention state.

## Scheduled ingestion

Create a plan first:

```powershell
python .\odp.py schedule-plan --frequency daily --start-time 02:00
```

Register only after reviewing the plan:

```powershell
.\scripts\register_task_scheduler.ps1 -WhatIf
.\scripts\register_task_scheduler.ps1 -TaskName "OpenDataPlatform-GLEIF" -Frequency Daily -StartTime "02:00"
```

The pipeline lock prevents overlapping runs. A `STALE` lock must be reviewed against the recorded PID, host, and start time before an operator removes it manually.

## API exposure

Keep the service on loopback unless remote access is required. Remote binding requires both `--allow-remote` and `--auth-token-file`; place the token outside the repository and do not put it in logs or source control.

```powershell
python .\odp.py serve --host 127.0.0.1 --port 8080
python .\odp.py serve --host 0.0.0.0 --allow-remote --auth-token-file "C:\\secrets\\odp.token"
```

Put TLS termination, firewall policy, and network access controls in the deployment boundary in front of the service.

## Backup verification and failover

Verify the secondary release before relying on it:

```powershell
python .\odp.py verify-replica <snapshot-id> --replica-root "E:\\open-data-releases"
```

Restore into a new, empty failover root. Restore refuses to overwrite an existing snapshot:

```powershell
python .\odp.py restore-release <snapshot-id> --replica-root "E:\\open-data-releases" --restore-root "F:\\odp-failover"
python .\odp.py lookup-lei <lei> --data-root "F:\\odp-failover"
```

The failover root contains the source manifest and query product. The primary raw and normalized payloads remain the authoritative archival copy.

## Incident handling

| Signal | Action |
| --- | --- |
| `DEPLOYMENT_NOT_READY` | Inspect the failed boundary; do not expose the service until verified. |
| `PIPELINE_LOCK_ACTIVE` | Wait for the active PID/run to finish; do not start a second run. |
| `PIPELINE_LOCK_STALE` | Confirm the PID is gone, then remove only the exact stale lock file. |
| `INTERRUPTED_ARTIFACTS_PRESENT` | Inspect `recovery-plan`; preserve evidence before cleanup. |
| Replica verification failure | Keep the primary in service and repair/reseed the replica. |
| Primary unavailable | Restore the last verified replica into a new failover root and validate a lookup. |

After recovery, run the monitor again and record the snapshot ID, release hash, and event log outcome.
