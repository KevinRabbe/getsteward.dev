from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from .errors import PlatformError
from .ingest import ingest_latest
from .verify import verify_snapshot


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="odp", description="Open Data Platform v0.1")
    sub = parser.add_subparsers(dest="command", required=True)

    ingest = sub.add_parser("ingest-gleif", help="Discover and archive the latest GLEIF Level 1 snapshot")
    ingest.add_argument("--source", type=Path, default=Path("config/sources/gleif_level1.json"))
    ingest.add_argument("--admission", type=Path, default=Path("config/admissions/gleif_level1.json"))
    ingest.add_argument("--data-root", type=Path, default=Path("data"))
    ingest.add_argument("--replica-root", type=Path, default=None)

    verify = sub.add_parser("verify", help="Verify one archived snapshot and its manifest")
    verify.add_argument("snapshot_id")
    verify.add_argument("--data-root", type=Path, default=Path("data"))

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
        else:
            raise RuntimeError("unreachable")
    except (PlatformError, OSError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(2)

    print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
