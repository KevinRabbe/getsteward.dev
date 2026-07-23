"""Zero-install launcher for the v0.1 project."""
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from open_data_platform.cli import main  # noqa: E402


if __name__ == "__main__":
    main()
