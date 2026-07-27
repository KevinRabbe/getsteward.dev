"""Zero-install Phase 22 commercial product candidate launcher."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from open_data_platform.commercial import (  # noqa: E402
    build_commercial_product,
    verify_commercial_product,
)
from open_data_platform.errors import PlatformError  # noqa: E402


def main() -> None:
    parser = argparse.ArgumentParser(prog="commercial_product")
    sub = parser.add_subparsers(dest="command", required=True)

    build = sub.add_parser("build")
    build.add_argument("entity_snapshot_id")
    build.add_argument("--relationship-snapshot-id", default=None)
    build.add_argument("--data-root", type=Path, default=Path("data"))
    build.add_argument("--catalog", type=Path, default=Path("catalog/products/global_legal_entity_history.json"))
    build.add_argument("--parquet", action="store_true")

    verify = sub.add_parser("verify")
    verify.add_argument("entity_snapshot_id")
    verify.add_argument("--relationship-snapshot-id", default=None)
    verify.add_argument("--data-root", type=Path, default=Path("data"))

    args = parser.parse_args()
    try:
        if args.command == "build":
            result = build_commercial_product(
                args.data_root,
                args.entity_snapshot_id,
                relationship_snapshot_id=args.relationship_snapshot_id,
                include_parquet=args.parquet,
                catalog_path=args.catalog,
            )
        else:
            result = verify_commercial_product(
                args.data_root,
                args.entity_snapshot_id,
                relationship_snapshot_id=args.relationship_snapshot_id,
            )
    except (PlatformError, OSError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(2)

    print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
