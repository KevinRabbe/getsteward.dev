"""Zero-install Phase 19 GLEIF RR-CDF pipeline and query launcher."""
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
from open_data_platform.relationship_pipeline import run_relationship_pipeline  # noqa: E402
from open_data_platform.relationship_query import relationships_for_lei  # noqa: E402


def main() -> None:
    parser = argparse.ArgumentParser(prog="gleif_relationships")
    sub = parser.add_subparsers(dest="command", required=True)

    run = sub.add_parser("run", help="Run the complete GLEIF RR-CDF pipeline")
    run.add_argument("--source", type=Path, default=Path("config/sources/gleif_relationships.json"))
    run.add_argument("--admission", type=Path, default=Path("config/admissions/gleif_relationships.json"))
    run.add_argument("--data-root", type=Path, default=Path("data"))
    run.add_argument("--replica-root", type=Path, default=None)

    query = sub.add_parser("query", help="Query verified RR-CDF relationships for one LEI")
    query.add_argument("snapshot_id")
    query.add_argument("lei")
    query.add_argument("--data-root", type=Path, default=Path("data"))
    query.add_argument("--direction", choices=("outgoing", "incoming", "both"), default="both")
    query.add_argument("--relationship-type", default=None)
    query.add_argument("--limit", type=int, default=500)

    args = parser.parse_args()
    try:
        if args.command == "run":
            result = run_relationship_pipeline(
                source_config=args.source,
                admission_config=args.admission,
                data_root=args.data_root,
                replica_root=args.replica_root,
            )
        else:
            result = relationships_for_lei(
                args.data_root,
                args.snapshot_id,
                args.lei,
                direction=args.direction,
                relationship_type=args.relationship_type,
                limit=args.limit,
            )
    except (PlatformError, OSError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(2)

    print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
