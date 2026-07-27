"""Zero-install Phase 18 historical-change launcher."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from open_data_platform.changes import build_changes, verify_changes  # noqa: E402
from open_data_platform.errors import PlatformError  # noqa: E402


def main() -> None:
    parser = argparse.ArgumentParser(
        prog="historical_changes",
        description="Build or verify a deterministic GLEIF historical change artifact.",
    )
    parser.add_argument("from_snapshot_id")
    parser.add_argument("to_snapshot_id")
    parser.add_argument("--data-root", type=Path, default=Path("data"))
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()

    try:
        if args.verify_only:
            result = verify_changes(args.data_root, args.from_snapshot_id, args.to_snapshot_id)
        else:
            result = build_changes(
                args.data_root,
                args.from_snapshot_id,
                args.to_snapshot_id,
                event_log=args.data_root / "events" / "events.jsonl",
            )
    except (PlatformError, OSError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(2)

    print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
