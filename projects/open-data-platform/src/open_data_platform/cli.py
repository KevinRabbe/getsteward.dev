from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from .errors import PlatformError
from .events import summarize_events
from .ingest import ingest_latest
from .parser import parse_snapshot, verify_normalized_artifact
from .pipeline import run_gleif_pipeline
from .product import build_product, verify_product
from .verify import verify_snapshot
from .query import lookup_lei, search_name
from .release import build_release, verify_release
from .operations import replicate_release, status_report
from .serve import serve
from .retention import retention_plan
from .deployment import deployment_readiness
from .runtime import pipeline_lock_status, recovery_plan
from .schedule import schedule_plan


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="odp", description="Open Data Platform v0.10")
    sub = parser.add_subparsers(dest="command", required=True)

    ingest = sub.add_parser("ingest-gleif", help="Discover and archive the latest GLEIF Level 1 snapshot")
    ingest.add_argument("--source", type=Path, default=Path("config/sources/gleif_level1.json"))
    ingest.add_argument("--admission", type=Path, default=Path("config/admissions/gleif_level1.json"))
    ingest.add_argument("--data-root", type=Path, default=Path("data"))
    ingest.add_argument("--replica-root", type=Path, default=None)

    verify = sub.add_parser("verify", help="Verify one archived snapshot and its manifest")
    verify.add_argument("snapshot_id")
    verify.add_argument("--data-root", type=Path, default=Path("data"))

    parse = sub.add_parser("parse-gleif", help="Stream one verified GLEIF snapshot into normalized JSONL")
    parse.add_argument("snapshot_id")
    parse.add_argument("--data-root", type=Path, default=Path("data"))
    parse.add_argument("--output-root", type=Path, default=None)

    normalized_verify = sub.add_parser("verify-normalized", help="Verify a normalized GLEIF artifact and every JSONL record")
    normalized_verify.add_argument("snapshot_id")
    normalized_verify.add_argument("--data-root", type=Path, default=Path("data"))
    normalized_verify.add_argument("--output-root", type=Path, default=None)

    build = sub.add_parser("build-gleif", help="Build a verified GLEIF normalized artifact into SQLite")
    build.add_argument("snapshot_id")
    build.add_argument("--data-root", type=Path, default=Path("data"))
    build.add_argument("--normalized-root", type=Path, default=None)
    build.add_argument("--output-root", type=Path, default=None)

    product_verify = sub.add_parser("verify-product", help="Verify one built GLEIF SQLite product")
    product_verify.add_argument("snapshot_id")
    product_verify.add_argument("--data-root", type=Path, default=Path("data"))
    product_verify.add_argument("--output-root", type=Path, default=None)

    release = sub.add_parser("build-release", help="Package a verified SQLite product with provenance")
    release.add_argument("snapshot_id")
    release.add_argument("--data-root", type=Path, default=Path("data"))
    release.add_argument("--normalized-root", type=Path, default=None)
    release.add_argument("--product-root", type=Path, default=None)
    release.add_argument("--output-root", type=Path, default=None)

    release_verify = sub.add_parser("verify-release", help="Verify one release ZIP and its payload checksums")
    release_verify.add_argument("snapshot_id")
    release_verify.add_argument("--data-root", type=Path, default=Path("data"))
    release_verify.add_argument("--output-root", type=Path, default=None)

    lookup = sub.add_parser("lookup-lei", help="Look up one LEI in a verified product")
    lookup.add_argument("lei")
    lookup.add_argument("--snapshot-id", default=None)
    lookup.add_argument("--data-root", type=Path, default=Path("data"))
    lookup.add_argument("--product-root", type=Path, default=None)

    search = sub.add_parser("search-name", help="Search legal names in a verified product")
    search.add_argument("query")
    search.add_argument("--snapshot-id", default=None)
    search.add_argument("--limit", type=int, default=20)
    search.add_argument("--data-root", type=Path, default=Path("data"))
    search.add_argument("--product-root", type=Path, default=None)

    pipeline = sub.add_parser("run-gleif", help="Run ingest, parse, product build, and release packaging")
    pipeline.add_argument("--source", type=Path, default=Path("config/sources/gleif_level1.json"))
    pipeline.add_argument("--admission", type=Path, default=Path("config/admissions/gleif_level1.json"))
    pipeline.add_argument("--data-root", type=Path, default=Path("data"))
    pipeline.add_argument("--replica-root", type=Path, default=None)
    pipeline.add_argument("--normalized-root", type=Path, default=None)
    pipeline.add_argument("--product-root", type=Path, default=None)
    pipeline.add_argument("--release-root", type=Path, default=None)

    events = sub.add_parser("events", help="Summarize the append-only pipeline event log")
    events.add_argument("--data-root", type=Path, default=Path("data"))
    events.add_argument("--limit", type=int, default=20)

    replicate = sub.add_parser("replicate-release", help="Copy a verified release bundle to a second root")
    replicate.add_argument("snapshot_id")
    replicate.add_argument("--replica-root", type=Path, required=True)
    replicate.add_argument("--data-root", type=Path, default=Path("data"))
    replicate.add_argument("--release-root", type=Path, default=None)

    status = sub.add_parser("status", help="Report verification status across archived snapshots")
    status.add_argument("--data-root", type=Path, default=Path("data"))
    status.add_argument("--event-limit", type=int, default=20)

    serve_parser = sub.add_parser("serve", help="Serve a verified product over a read-only HTTP API")
    serve_parser.add_argument("--data-root", type=Path, default=Path("data"))
    serve_parser.add_argument("--product-root", type=Path, default=None)
    serve_parser.add_argument("--snapshot-id", default=None)
    serve_parser.add_argument("--host", default="127.0.0.1")
    serve_parser.add_argument("--port", type=int, default=8080)

    retention = sub.add_parser("retention-plan", help="Create a non-destructive retention and storage report")
    retention.add_argument("--data-root", type=Path, default=Path("data"))
    retention.add_argument("--keep-latest", type=int, default=7)

    deployment = sub.add_parser("deployment-check", help="Report whether a verified release is deployment-ready")
    deployment.add_argument("--data-root", type=Path, default=Path("data"))
    deployment.add_argument("--snapshot-id", default=None)
    deployment.add_argument("--release-root", type=Path, default=None)
    deployment.add_argument("--product-root", type=Path, default=None)
    deployment.add_argument("--replica-root", type=Path, default=None)

    lock = sub.add_parser("pipeline-lock", help="Report whether a scheduled pipeline lock is clear, active, or stale")
    lock.add_argument("--data-root", type=Path, default=Path("data"))

    recovery = sub.add_parser("recovery-plan", help="Report interrupted pipeline artifacts without deleting anything")
    recovery.add_argument("--data-root", type=Path, default=Path("data"))

    schedule = sub.add_parser("schedule-plan", help="Create a scheduler registration plan without changing the host")
    schedule.add_argument("--project-root", type=Path, default=Path("."))
    schedule.add_argument("--python-exe", default="python")
    schedule.add_argument("--data-root", type=Path, default=Path("data"))
    schedule.add_argument("--replica-root", type=Path, default=None)
    schedule.add_argument("--frequency", choices=("daily", "weekly"), default="daily")
    schedule.add_argument("--start-time", default="02:00")

    return parser


def main() -> None:
    args = build_parser().parse_args()
    try:
        if args.command == "ingest-gleif":
            result = ingest_latest(
                source_config=args.source,
                admission_config=args.admission,
                data_root=args.data_root,
                replica_root=args.replica_root,
            )
        elif args.command == "verify":
            result = verify_snapshot(args.data_root, args.snapshot_id)
        elif args.command == "parse-gleif":
            result = parse_snapshot(
                args.data_root,
                args.snapshot_id,
                output_root=args.output_root,
                event_log=args.data_root / "events" / "events.jsonl",
            )
        elif args.command == "verify-normalized":
            result = verify_normalized_artifact(
                args.data_root,
                args.snapshot_id,
                output_root=args.output_root,
            )
        elif args.command == "build-gleif":
            result = build_product(
                args.data_root,
                args.snapshot_id,
                normalized_root=args.normalized_root,
                output_root=args.output_root,
                event_log=args.data_root / "events" / "events.jsonl",
            )
        elif args.command == "verify-product":
            result = verify_product(
                args.data_root,
                args.snapshot_id,
                output_root=args.output_root,
            )
        elif args.command == "build-release":
            result = build_release(
                args.data_root,
                args.snapshot_id,
                normalized_root=args.normalized_root,
                product_root=args.product_root,
                output_root=args.output_root,
                event_log=args.data_root / "events" / "events.jsonl",
            )
        elif args.command == "verify-release":
            result = verify_release(args.data_root, args.snapshot_id, output_root=args.output_root)
        elif args.command == "lookup-lei":
            result = lookup_lei(
                args.data_root,
                args.lei,
                snapshot_id=args.snapshot_id,
                product_root=args.product_root,
            )
        elif args.command == "search-name":
            result = search_name(
                args.data_root,
                args.query,
                snapshot_id=args.snapshot_id,
                product_root=args.product_root,
                limit=args.limit,
            )
        elif args.command == "run-gleif":
            result = run_gleif_pipeline(
                source_config=args.source,
                admission_config=args.admission,
                data_root=args.data_root,
                replica_root=args.replica_root,
                normalized_root=args.normalized_root,
                product_root=args.product_root,
                release_root=args.release_root,
            )
        elif args.command == "events":
            result = summarize_events(args.data_root / "events" / "events.jsonl", limit=args.limit)
        elif args.command == "replicate-release":
            result = replicate_release(
                args.data_root,
                args.snapshot_id,
                args.replica_root,
                release_root=args.release_root,
            )
        elif args.command == "status":
            result = status_report(args.data_root, event_limit=args.event_limit)
        elif args.command == "serve":
            serve(
                args.data_root,
                product_root=args.product_root,
                snapshot_id=args.snapshot_id,
                host=args.host,
                port=args.port,
            )
            return
        elif args.command == "retention-plan":
            result = retention_plan(args.data_root, keep_latest=args.keep_latest)
        elif args.command == "deployment-check":
            result = deployment_readiness(
                args.data_root,
                snapshot_id=args.snapshot_id,
                release_root=args.release_root,
                product_root=args.product_root,
                replica_root=args.replica_root,
            )
        elif args.command == "pipeline-lock":
            result = pipeline_lock_status(args.data_root)
        elif args.command == "recovery-plan":
            result = recovery_plan(args.data_root)
        elif args.command == "schedule-plan":
            result = schedule_plan(
                project_root=args.project_root,
                python_exe=args.python_exe,
                data_root=args.data_root,
                replica_root=args.replica_root,
                frequency=args.frequency,
                start_time=args.start_time,
            )
        else:
            raise RuntimeError("unreachable")
    except (PlatformError, OSError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(2)

    print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
