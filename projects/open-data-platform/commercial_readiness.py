"""Zero-install commercial legal-review and technical sellability gate launcher."""
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
from open_data_platform.sellability import (  # noqa: E402
    build_sale_envelope,
    legal_review_packet,
    sellability_report,
    verify_sale_envelope,
    verify_terms_approval,
)


def main() -> None:
    parser = argparse.ArgumentParser(prog="commercial_readiness")
    sub = parser.add_subparsers(dest="command", required=True)

    packet = sub.add_parser(
        "packet",
        help="Produce the factual legal-review handoff for one product candidate",
    )
    packet.add_argument("product_id")
    packet.add_argument("--project-root", type=Path, default=Path("."))

    approval = sub.add_parser(
        "verify-approval",
        help="Verify a human-supplied approval record and exact terms-document hash",
    )
    approval.add_argument("approval_path", type=Path)
    approval.add_argument("--product-id", default=None)

    status = sub.add_parser(
        "status",
        help="Report technical sale readiness without mutating the candidate",
    )
    status.add_argument("product_id")
    status.add_argument("snapshot_id")
    status.add_argument("--project-root", type=Path, default=Path("."))
    status.add_argument("--data-root", type=Path, default=Path("data"))
    status.add_argument("--terms-approval", type=Path, default=None)

    bind = sub.add_parser(
        "bind",
        help="Create an immutable sale envelope only when all technical gates pass",
    )
    bind.add_argument("product_id")
    bind.add_argument("snapshot_id")
    bind.add_argument("--terms-approval", type=Path, required=True)
    bind.add_argument("--project-root", type=Path, default=Path("."))
    bind.add_argument("--data-root", type=Path, default=Path("data"))

    verify = sub.add_parser(
        "verify-envelope",
        help="Re-verify one immutable candidate/terms/operational sale binding",
    )
    verify.add_argument("product_id")
    verify.add_argument("snapshot_id")
    verify.add_argument("terms_id")
    verify.add_argument("--project-root", type=Path, default=Path("."))
    verify.add_argument("--data-root", type=Path, default=Path("data"))

    args = parser.parse_args()
    try:
        if args.command == "packet":
            result = legal_review_packet(args.project_root, args.product_id)
        elif args.command == "verify-approval":
            result = verify_terms_approval(
                args.approval_path,
                product_id=args.product_id,
            )
        elif args.command == "status":
            result = sellability_report(
                project_root=args.project_root,
                data_root=args.data_root,
                product_id=args.product_id,
                snapshot_id=args.snapshot_id,
                terms_approval_path=args.terms_approval,
            )
        elif args.command == "bind":
            result = build_sale_envelope(
                project_root=args.project_root,
                data_root=args.data_root,
                product_id=args.product_id,
                snapshot_id=args.snapshot_id,
                terms_approval_path=args.terms_approval,
            )
        else:
            result = verify_sale_envelope(
                project_root=args.project_root,
                data_root=args.data_root,
                product_id=args.product_id,
                snapshot_id=args.snapshot_id,
                terms_id=args.terms_id,
            )
    except (PlatformError, OSError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(2)

    print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
