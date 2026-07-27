"""Zero-install ROR commercial history product candidate launcher."""
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
from open_data_platform.ror_commercial import (  # noqa: E402
    build_ror_commercial_product,
    verify_ror_commercial_product,
)


def main() -> None:
    parser = argparse.ArgumentParser(prog="ror_commercial_product")
    sub = parser.add_subparsers(dest="command", required=True)

    build = sub.add_parser("build")
    build.add_argument("snapshot_id")
    build.add_argument("--data-root", type=Path, default=Path("data"))
    build.add_argument(
        "--catalog",
        type=Path,
        default=Path("catalog/products/global_research_organization_history.json"),
    )
    build.add_argument("--parquet", action="store_true")

    verify = sub.add_parser("verify")
    verify.add_argument("snapshot_id")
    verify.add_argument("--data-root", type=Path, default=Path("data"))

    args = parser.parse_args()
    try:
        if args.command == "build":
            result = build_ror_commercial_product(
                args.data_root,
                args.snapshot_id,
                include_parquet=args.parquet,
                catalog_path=args.catalog,
            )
        else:
            result = verify_ror_commercial_product(
                args.data_root,
                args.snapshot_id,
            )
    except (PlatformError, OSError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(2)

    print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
