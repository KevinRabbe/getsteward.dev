from __future__ import annotations

import hmac
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import parse_qs, unquote, urlsplit

from .errors import QueryError
from .query import QueryStore


def _json_bytes(value: dict[str, Any]) -> bytes:
    return (json.dumps(value, ensure_ascii=False, sort_keys=True) + "\n").encode("utf-8")


def _read_auth_token(path: Path | None) -> str | None:
    if path is None:
        return None
    try:
        token = path.read_text(encoding="utf-8").strip()
    except OSError as exc:
        raise QueryError(f"Could not read auth token file: {path}") from exc
    if not token:
        raise QueryError(f"Auth token file is empty: {path}")
    return token


def _handler_for(store: QueryStore, auth_token: str | None):
    class Handler(BaseHTTPRequestHandler):
        server_version = "OpenDataPlatform/0.11"
        sys_version = ""

        def _respond(
            self,
            status: int,
            value: dict[str, Any],
            *,
            headers: dict[str, str] | None = None,
        ) -> None:
            payload = _json_bytes(value)
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(payload)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("X-Frame-Options", "DENY")
            if headers:
                for name, value in headers.items():
                    self.send_header(name, value)
            self.end_headers()
            self.wfile.write(payload)

        def _authorized(self) -> bool:
            if auth_token is None:
                return True
            authorization = self.headers.get("Authorization", "")
            expected = "Bearer " + auth_token
            if not hmac.compare_digest(authorization, expected):
                self._respond(
                    401,
                    {"status": "UNAUTHORIZED", "error": "Bearer authentication required"},
                    headers={"WWW-Authenticate": "Bearer"},
                )
                return False
            return True

        def do_GET(self) -> None:  # noqa: N802
            if not self._authorized():
                return
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
                    if len(name) > 500:
                        raise QueryError("Name query must not exceed 500 characters")
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
    auth_token_file: Path | None = None,
    allow_remote: bool = False,
) -> ThreadingHTTPServer:
    is_loopback = host in {"127.0.0.1", "::1", "localhost"}
    if not is_loopback and not allow_remote:
        raise QueryError("Remote binding requires --allow-remote")
    auth_token = _read_auth_token(auth_token_file)
    if not is_loopback and auth_token is None:
        raise QueryError("Remote binding requires --auth-token-file")
    store = QueryStore(data_root, snapshot_id=snapshot_id, product_root=product_root)
    server = ThreadingHTTPServer((host, port), _handler_for(store, auth_token))
    server.daemon_threads = True
    server.odp_store = store  # type: ignore[attr-defined]
    return server


def serve(
    data_root: Path,
    *,
    snapshot_id: str | None = None,
    product_root: Path | None = None,
    host: str = "127.0.0.1",
    port: int = 8080,
    auth_token_file: Path | None = None,
    allow_remote: bool = False,
) -> None:
    server = create_server(
        data_root,
        snapshot_id=snapshot_id,
        product_root=product_root,
        host=host,
        port=port,
        auth_token_file=auth_token_file,
        allow_remote=allow_remote,
    )
    try:
        print(f"Open Data Platform serving on http://{host}:{server.server_port}")
        server.serve_forever()
    finally:
        server.server_close()
