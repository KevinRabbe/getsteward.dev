from __future__ import annotations

import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import parse_qs, unquote, urlsplit

from .errors import QueryError
from .query import QueryStore


def _json_bytes(value: dict[str, Any]) -> bytes:
    return (json.dumps(value, ensure_ascii=False, sort_keys=True) + "\n").encode("utf-8")


def _handler_for(store: QueryStore):
    class Handler(BaseHTTPRequestHandler):
        server_version = "OpenDataPlatform/0.8"

        def _respond(self, status: int, value: dict[str, Any]) -> None:
            payload = _json_bytes(value)
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(payload)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(payload)

        def do_GET(self) -> None:  # noqa: N802
            parsed = urlsplit(self.path)
            params = parse_qs(parsed.query, keep_blank_values=True)
            try:
                if parsed.path == "/healthz":
                    self._respond(200, store.health())
                    return
                if parsed.path == "/v1/status":
                    self._respond(200, store.health())
                    return
                if parsed.path.startswith("/v1/lei/"):
                    lei = unquote(parsed.path[len("/v1/lei/"):])
                    result = store.lookup(lei)
                    response_status = {
                        "FOUND": 200,
                        "AMBIGUOUS": 409,
                        "NOT_FOUND": 404,
                    }.get(result["status"], 500)
                    self._respond(response_status, result)
                    return
                if parsed.path == "/v1/search":
                    name = params.get("name", [""])[0]
                    limit = int(params.get("limit", ["20"])[0])
                    self._respond(200, store.search(name, limit=limit))
                    return
                self._respond(404, {"status": "NOT_FOUND", "error": "Route not found"})
            except (QueryError, ValueError) as exc:
                self._respond(400, {"status": "BAD_REQUEST", "error": str(exc)})
            except Exception as exc:  # pragma: no cover - defensive HTTP boundary
                self._respond(500, {"status": "ERROR", "error": str(exc)})

        def log_message(self, format: str, *args: Any) -> None:
            return

    return Handler


def create_server(
    data_root: Path,
    *,
    snapshot_id: str | None = None,
    product_root: Path | None = None,
    host: str = "127.0.0.1",
    port: int = 8080,
) -> ThreadingHTTPServer:
    store = QueryStore(data_root, snapshot_id=snapshot_id, product_root=product_root)
    server = ThreadingHTTPServer((host, port), _handler_for(store))
    server.odp_store = store  # type: ignore[attr-defined]
    return server


def serve(
    data_root: Path,
    *,
    snapshot_id: str | None = None,
    product_root: Path | None = None,
    host: str = "127.0.0.1",
    port: int = 8080,
) -> None:
    server = create_server(
        data_root,
        snapshot_id=snapshot_id,
        product_root=product_root,
        host=host,
        port=port,
    )
    try:
        print(f"Open Data Platform serving on http://{host}:{server.server_port}")
        server.serve_forever()
    finally:
        server.server_close()
