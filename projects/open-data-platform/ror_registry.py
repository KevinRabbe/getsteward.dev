"""Zero-install ROR organizations pipeline, history, and query launcher."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from open_data_platform.errors import PlatformError  # noqa: E402
from open_data_platform.ror_changes import build_ror_changes, verify_ror_changes  # noqa: E402
from open_data_platform.ror_pipeline import run_ror_pipeline  # noqa: E402
from open_data_platform.ror_query import lookup_ror, search_ror_name  # noqa: E402


def _pipeline_arguments(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--source", type=Path, default=Path("config/sources/ror_registry.json"))
    parser.add_argument("--admission", type=Path, default=Path("config/admissions/ror_registry.json"))
    parser.add_argument("--data-root", type=Path, default=Path("data"))
    parser.add_argument("--replica-root", type=Path, default=None)


def main() -> None:
    parser = argparse.ArgumentParser(prog="ror_registry")
    sub = parser.add_subparsers(dest="command", required=True)

    run = sub.add_parser("run", help="Run the complete latest ROR data-dump pipeline")
    _pipeline_arguments(run)

    backfill = sub.add_parser(
        "backfill",
        help="Run the complete ROR pipeline for one explicit Zenodo version record",
    )
    backfill.add_argument("record_id", type=int)
    _pipeline_arguments(backfill)

    changes = sub.add_parser("changes", help="Build or verify one ROR historical change pair")
    changes.add_argument("from_snapshot_id")
    changes.add_argument("to_snapshot_id")
    changes.add_argument("--data-root", type=Path, default=Path("data"))
    changes.add_argument("--verify-only", action="store_true")

    lookup = sub.add_parser("lookup", help="Look up one organization by ROR ID")
    lookup.add_argument("snapshot_id")
    lookup.add_argument("ror_id")
    lookup.add_argument("--data-root", type=Path, default=Path("data"))

    search = sub.add_parser("search", help="Search ROR display names")
    search.add_argument("snapshot_id")
    search.add_argument("query")
    search.add_argument("--data-root", type=Path, default=Path("data"))
    search.add_argument("--limit", type=int, default=20)

    args = parser.parse_args()
    try:
        if args.command == "run":
            result = run_ror_pipeline(
                source_config=args.source,
                admission_config=args.admission,
                data_root=args.data_root,
                replica_root=args.replica_root,
            )
        elif args.command == "backfill":
            if args.record_id < 1:
                raise ValueError("record_id must be a positive integer")
            result = run_ror_pipeline(
                source_config=args.source,
                admission_config=args.admission,
                data_root=args.data_root,
                replica_root=args.replica_root,
                zenodo_record_id=args.record_id,
            )
        elif args.command == "changes":
            if args.verify_only:
                result = verify_ror_changes(
                    args.data_root,
                    args.from_snapshot_id,
                    args.to_snapshot_id,
                )
            else:
                result = build_ror_changes(
                    args.data_root,
                    args.from_snapshot_id,
                    args.to_snapshot_id,
                    event_log=args.data_root / "events" / "events.jsonl",
                )
        elif args.command == "lookup":
            result = lookup_ror(args.data_root, args.snapshot_id, args.ror_id)
        else:
            result = search_ror_name(
                args.data_root,
                args.snapshot_id,
                args.query,
                limit=args.limit,
            )
    except (PlatformError, OSError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(2)

    print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
