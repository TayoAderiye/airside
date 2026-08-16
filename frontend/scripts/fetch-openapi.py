#!/usr/bin/env python3
"""Download GET /openapi/v1.json after completing first-run setup if needed."""

from __future__ import annotations

import json
import os
import re
import sys
import urllib.error
import urllib.request
from http.cookiejar import CookieJar
from pathlib import Path

API = os.environ.get("AIRSIDE_API_URL", "http://127.0.0.1:5109")
OUT = Path(sys.argv[1] if len(sys.argv) > 1 else "lib/api/openapi.json")
LOG = Path(os.environ["AIRSIDE_API_LOG"])
EMAIL = os.environ.get("AIRSIDE_DEV_EMAIL", "admin@airside.local")
PASSWORD = os.environ.get("AIRSIDE_DEV_PASSWORD", "AirsideDev1!")


def request(opener: urllib.request.OpenerDirector, method: str, path: str, body: dict | None = None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(
        API + path,
        data=data,
        method=method,
        headers={"Content-Type": "application/json", "Accept": "application/json"},
    )
    try:
        with opener.open(req) as res:
            raw = res.read()
            return res.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as exc:
        raw = exc.read()
        try:
            payload = json.loads(raw) if raw else None
        except json.JSONDecodeError:
            payload = raw.decode(errors="replace")
        return exc.code, payload


def token_from_log() -> str:
    text = LOG.read_text(errors="replace")
    match = re.search(r"one-time setup token:.*?│\s+([A-Za-z0-9_-]{20,})\s+│", text, re.S)
    if not match:
        raise SystemExit("setup token not found in AIRSIDE_API_LOG")
    return match.group(1)


def main() -> None:
    jar = CookieJar()
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))

    status, payload = request(opener, "GET", "/api/v1/setup/status")
    if status != 200:
        raise SystemExit(f"setup/status failed: {status} {payload}")

    if not payload.get("setupCompleted"):
        complete_status, complete = request(
            opener,
            "POST",
            "/api/v1/setup/complete",
            {
                "setupToken": token_from_log(),
                "email": EMAIL,
                "password": PASSWORD,
                "displayName": "Admin",
                "instanceName": "dev",
            },
        )
        if complete_status not in (200, 409):
            raise SystemExit(f"setup/complete failed: {complete_status} {complete}")

    login_status, login = request(
        opener,
        "POST",
        "/api/v1/auth/login",
        {"email": EMAIL, "password": PASSWORD},
    )
    if login_status != 200:
        raise SystemExit(f"login failed: {login_status} {login}")

    spec_req = urllib.request.Request(API + "/openapi/v1.json", headers={"Accept": "application/json"})
    with opener.open(spec_req) as res:
        spec = res.read()
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_bytes(spec)
    doc = json.loads(spec)
    print(f"wrote {OUT} ({len(spec)} bytes, {len(doc.get('paths', {}))} paths)")


if __name__ == "__main__":
    main()
