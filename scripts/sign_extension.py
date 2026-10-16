#!/usr/bin/env python3
"""The Sofabird TV extension (companion/tv-extension): packing, signing by Mozilla, and the check CI runs.

Release Firefox loads only extensions signed by Mozilla. The extension is signed "unlisted" (not published on
addons.mozilla.org, signed automatically) and the signed XPI is committed next to its source, so that builds need no
keys; the companion embeds it and installs it into its TV browser profile.

  python scripts/sign_extension.py sign    pack src/, sign it on AMO, save companion/tv-extension/tv@sofabird.app.xpi
  python scripts/sign_extension.py check   the committed XPI holds exactly src/ (CI; after editing src/, bump the
                                           version in manifest.json and sign again)
  python scripts/sign_extension.py pack    only pack src/ into companion/tv-extension/build/unsigned.xpi

Keys (AMO → Developer Hub → "Manage API Keys"): the environment variables AMO_JWT_ISSUER and AMO_JWT_SECRET (CI
secrets), or %USERPROFILE%\\.sofabird\\amo.json {"issuer": "...", "secret": "..."} on a developer PC. Only the
standard library is used.
"""

import base64
import hashlib
import hmac
import io
import json
import os
import secrets
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXT_DIR = os.path.join(ROOT, "companion", "tv-extension")
SRC_DIR = os.path.join(EXT_DIR, "src")
API = "https://addons.mozilla.org/api/v5"
SIGN_TIMEOUT_S = 15 * 60


def manifest():
    with open(os.path.join(SRC_DIR, "manifest.json"), encoding="utf-8") as f:
        return json.load(f)


def addon_id():
    return manifest()["browser_specific_settings"]["gecko"]["id"]


def signed_path():
    return os.path.join(EXT_DIR, addon_id() + ".xpi")


def source_files():
    """Relative path (with "/") → bytes of every file under src/."""
    files = {}
    for folder, _, names in os.walk(SRC_DIR):
        for name in names:
            path = os.path.join(folder, name)
            rel = os.path.relpath(path, SRC_DIR).replace(os.sep, "/")
            with open(path, "rb") as f:
                files[rel] = f.read()
    return files


def pack():
    """A deterministic XPI of src/ (sorted entries, fixed timestamps), as bytes."""
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as z:
        for rel, data in sorted(source_files().items()):
            info = zipfile.ZipInfo(rel, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            z.writestr(info, data)
    return buffer.getvalue()


def check():
    path = signed_path()
    if not os.path.exists(path):
        return fail(f"{os.path.relpath(path, ROOT)} is missing: run 'python scripts/sign_extension.py sign'")
    with zipfile.ZipFile(path) as z:
        signed = {n: z.read(n) for n in z.namelist() if not n.startswith("META-INF/") and not n.endswith("/")}
        signature = [n for n in z.namelist() if n.startswith("META-INF/")]
    if not signature:
        return fail(f"{os.path.relpath(path, ROOT)} is not signed")
    source = source_files()
    changed = sorted(set(signed) ^ set(source) | {n for n in set(signed) & set(source) if not same(n, signed[n], source[n])})
    if changed:
        return fail("the signed extension differs from companion/tv-extension/src (" + ", ".join(changed) + "): bump the "
                    "version in manifest.json and run 'python scripts/sign_extension.py sign'")
    print(f"The signed extension matches its source (version {manifest()['version']}).")
    return 0


def same(name, signed, source):
    """AMO re-indents manifest.json when it signs; its content must still be the same."""
    if name == "manifest.json":
        return json.loads(signed) == json.loads(source)
    return signed == source


def fail(message):
    print("ERROR: " + message, file=sys.stderr)
    return 1


# ------------------------------------------------------------------ AMO

def credentials():
    issuer, secret = os.environ.get("AMO_JWT_ISSUER"), os.environ.get("AMO_JWT_SECRET")
    if not (issuer and secret):
        path = os.path.join(os.path.expanduser("~"), ".sofabird", "amo.json")
        if os.path.exists(path):
            with open(path, encoding="utf-8") as f:
                keys = json.load(f)
            issuer, secret = keys.get("issuer"), keys.get("secret")
    if not (issuer and secret):
        raise SystemExit("ERROR: no AMO keys: set AMO_JWT_ISSUER and AMO_JWT_SECRET or create ~/.sofabird/amo.json")
    return issuer, secret


def b64url(data):
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def token(issuer, secret):
    now = int(time.time())
    header = b64url(json.dumps({"alg": "HS256", "typ": "JWT"}).encode())
    payload = b64url(json.dumps({"iss": issuer, "jti": secrets.token_hex(16), "iat": now, "exp": now + 60}).encode())
    signature = hmac.new(secret.encode(), f"{header}.{payload}".encode(), hashlib.sha256).digest()
    return f"{header}.{payload}.{b64url(signature)}"


def request(keys, method, url, body=None, content_type=None, raw=False):
    headers = {"Authorization": "JWT " + token(*keys), "User-Agent": "sofabird-sign-extension/1"}
    if content_type:
        headers["Content-Type"] = content_type
    req = urllib.request.Request(url if url.startswith("http") else API + url, data=body, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=120) as response:
            data = response.read()
            return response.status, (data if raw else json.loads(data or b"null"))
    except urllib.error.HTTPError as e:
        data = e.read()
        try:
            return e.code, json.loads(data)
        except ValueError:
            return e.code, data.decode("utf-8", "replace")


def upload(keys, xpi):
    boundary = "----sofabird" + secrets.token_hex(12)
    parts = [
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"channel\"\r\n\r\nunlisted\r\n".encode(),
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"upload\"; filename=\"extension.xpi\"\r\n"
        "Content-Type: application/x-xpinstall\r\n\r\n".encode() + xpi + b"\r\n",
        f"--{boundary}--\r\n".encode(),
    ]
    status, result = request(keys, "POST", "/addons/upload/", b"".join(parts), f"multipart/form-data; boundary={boundary}")
    if status >= 300:
        raise SystemExit(f"ERROR: upload failed ({status}): {result}")
    uuid = result["uuid"]
    print(f"Uploaded, validating ({uuid})...")
    deadline = time.time() + SIGN_TIMEOUT_S
    while not result.get("processed"):
        if time.time() > deadline:
            raise SystemExit("ERROR: validation did not finish in time")
        time.sleep(5)
        status, result = request(keys, "GET", f"/addons/upload/{uuid}/")
    if not result.get("valid"):
        messages = result.get("validation", {}).get("messages", [])
        errors = [f"- {m.get('message')} ({m.get('file')})" for m in messages if m.get("type") == "error"]
        raise SystemExit("ERROR: the extension did not pass AMO's validation:\n" + "\n".join(errors or [json.dumps(result)]))
    for m in result.get("validation", {}).get("messages", []):
        if m.get("type") == "warning":
            print(f"  warning: {m.get('message')} ({m.get('file')})")
    return uuid


def submit(keys, uuid):
    guid = urllib.parse.quote(addon_id(), safe="")
    status, _ = request(keys, "GET", f"/addons/addon/{guid}/")
    if status == 404:
        print("A new add-on on AMO (unlisted).")
        status, result = request(keys, "POST", "/addons/addon/", json.dumps({"version": {"upload": uuid}}).encode(),
                                 "application/json")
        version = result.get("latest_unlisted_version") or result.get("version") if status < 300 else None
    else:
        status, version = request(keys, "POST", f"/addons/addon/{guid}/versions/", json.dumps({"upload": uuid}).encode(),
                                  "application/json")
        result = version
    if status >= 300 or not version:
        raise SystemExit(f"ERROR: submitting the version failed ({status}): {result}")
    return guid, version["id"]


def wait_signed(keys, guid, version_id):
    print("Waiting for Mozilla's signature...")
    deadline = time.time() + SIGN_TIMEOUT_S
    while True:
        status, version = request(keys, "GET", f"/addons/addon/{guid}/versions/{version_id}/")
        file = version.get("file") if status < 300 else None
        if file and file.get("status") == "public" and file.get("url"):
            return file["url"]
        if file and file.get("status") == "disabled":
            raise SystemExit("ERROR: Mozilla rejected the version: " + json.dumps(version))
        if time.time() > deadline:
            raise SystemExit("ERROR: the version was not signed in time; run 'sign' again later to download it")
        time.sleep(10)


def sign():
    keys = credentials()
    xpi = pack()
    print(f"Packed {addon_id()} {manifest()['version']} ({len(xpi)} bytes).")
    uuid = upload(keys, xpi)
    guid, version_id = submit(keys, uuid)
    url = wait_signed(keys, guid, version_id)
    status, data = request(keys, "GET", url, raw=True)
    if status >= 300:
        raise SystemExit(f"ERROR: downloading the signed file failed ({status})")
    with open(signed_path(), "wb") as f:
        f.write(data)
    print(f"Signed: {os.path.relpath(signed_path(), ROOT)}")
    return check()


def main(argv):
    command = argv[1] if len(argv) > 1 else "check"
    if command == "check":
        return check()
    if command == "pack":
        os.makedirs(os.path.join(EXT_DIR, "build"), exist_ok=True)
        out = os.path.join(EXT_DIR, "build", "unsigned.xpi")
        with open(out, "wb") as f:
            f.write(pack())
        print(f"Packed: {os.path.relpath(out, ROOT)}")
        return 0
    if command == "sign":
        return sign()
    return fail(f"unknown command '{command}' (sign, check, pack)")


if __name__ == "__main__":
    sys.exit(main(sys.argv))
