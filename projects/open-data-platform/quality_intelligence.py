"""Zero-install Phase 21 quality profile and timeline launcher."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from open_data_platform.analytics import (  # noqa: E402
    build_quality_profile,
    quality_timeline,
    verify_quality_profile,
)
from open_data_platform.errors import PlatformError  # noqa: E402


def main() -> None:
    parser = argparse.ArgumentParser(prog="quality_intelligence")
    sub = parser.add_subparsers(dest="command", required=True)

    build = sub.add_parser("build")
    build.add_argument("snapshot_id")
    build.add_argument("--data-root", type=Path, default=Path("data"))

    verify = sub.add_parser("verify")
    verify.add_argument("snapshot_id")
    verify.add_argument("--data-root", type=Path, default=Path("data"))

    timeline = sub.add_parser("timeline")
    timeline.add_argument("source_dataset_id")
    timeline.add_argument("--data-root", type=Path, default=Path("data"))

    args = parser.parse_args()
    try:
        if args.command == "build":
            result = build_quality_profile(args.data_root, args.snapshot_id)
        elif args.command == "verify":
            result = verify_quality_profile(args.data_root, args.snapshot_id)
        else:
            result = quality_timeline(args.data_root, args.source_dataset_id)
    except (PlatformError, OSError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(2)

    print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
