"""Zero-install Phase 17 production-acceptance report launcher."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from open_data_platform.production import production_acceptance_report  # noqa: E402


def main() -> None:
    parser = argparse.ArgumentParser(
        prog="production_acceptance",
        description="Evaluate real GLEIF pipeline evidence across distinct source versions.",
    )
    parser.add_argument("--data-root", type=Path, default=Path("data"))
    parser.add_argument("--required-versions", type=int, default=7)
    args = parser.parse_args()

    result = production_acceptance_report(
        args.data_root,
        required_versions=args.required_versions,
    )
    print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))
    if result["status"] == "ALERT":
        raise SystemExit(3)


if __name__ == "__main__":
    main()
