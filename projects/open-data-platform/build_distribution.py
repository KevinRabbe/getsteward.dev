"""Zero-install Phase 20 distribution launcher (Parquet requires optional extra)."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from open_data_platform.distributions import (  # noqa: E402
    build_csv_distribution,
    build_parquet_distribution,
    verify_distribution,
)
from open_data_platform.errors import PlatformError  # noqa: E402


def main() -> None:
    parser = argparse.ArgumentParser(prog="build_distribution")
    parser.add_argument("snapshot_id")
    parser.add_argument("format", choices=("csv", "parquet"))
    parser.add_argument("--data-root", type=Path, default=Path("data"))
    parser.add_argument("--verify-only", action="store_true")
    parser.add_argument("--batch-size", type=int, default=10_000)
    args = parser.parse_args()

    try:
        if args.verify_only:
            result = verify_distribution(args.data_root, args.snapshot_id, args.format)
        elif args.format == "csv":
            result = build_csv_distribution(args.data_root, args.snapshot_id)
        else:
            result = build_parquet_distribution(
                args.data_root,
                args.snapshot_id,
                batch_size=args.batch_size,
            )
    except (PlatformError, OSError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(2)

    print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
