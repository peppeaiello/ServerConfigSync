"""Uploads a package to Thunderstore (Valheim community).

Usage: python3 tools/publish_thunderstore.py dist/ServerConfigSync-<version>.zip <team> [--validate-only]
The API token is read from TS_TOKEN or from ~/.config/serverconfigsync/thunderstore-token.
"""
import base64, json, os, sys, urllib.request, urllib.error, zipfile

API = "https://thunderstore.io"
TOKEN_FILE = os.path.expanduser("~/.config/serverconfigsync/thunderstore-token")
TOKEN = os.environ.get("TS_TOKEN") or open(TOKEN_FILE).read().strip()
zip_path, team = sys.argv[1], sys.argv[2]
validate_only = "--validate-only" in sys.argv
CATEGORIES = ["mods", "server-side", "client-side", "utility", "ai-generated"]


def call(method, url, body=None, auth=True, raw=None):
    data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("User-Agent", "ServerConfigSync-publisher/1.0")
    if raw is None:
        req.add_header("Content-Type", "application/json")
    if auth:
        req.add_header("Authorization", "Bearer " + TOKEN)
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            text = resp.read().decode() if raw is None else ""
            return resp.status, (json.loads(text) if text else None), resp.headers
    except urllib.error.HTTPError as e:
        print(f"HTTP {e.code} {url.split('?')[0]}: {e.read().decode()[:2000]}")
        sys.exit(1)


with zipfile.ZipFile(zip_path) as z:
    manifest = z.read("manifest.json")
status, result, _ = call("POST", f"{API}/api/experimental/submission/validate/manifest-v1/",
                         {"namespace": team, "manifest_data": base64.b64encode(manifest).decode()})
print("manifest validation:", status, result)
if validate_only:
    sys.exit(0)

size = os.path.getsize(zip_path)
_, init, _ = call("POST", f"{API}/api/experimental/usermedia/initiate-upload/",
                  {"filename": os.path.basename(zip_path), "file_size_bytes": size})
uuid = init["user_media"]["uuid"]
with open(zip_path, "rb") as f:
    content = f.read()
parts = []
for part in init["upload_urls"]:
    chunk = content[part["offset"]:part["offset"] + part["length"]]
    _, _, headers = call("PUT", part["url"], raw=chunk, auth=False)
    parts.append({"ETag": headers["ETag"], "PartNumber": part["part_number"]})
call("POST", f"{API}/api/experimental/usermedia/{uuid}/finish-upload/", {"parts": parts})
print("upload finished:", uuid)

status, result, _ = call("POST", f"{API}/api/experimental/submission/submit/", {
    "author_name": team, "upload_uuid": uuid, "communities": ["valheim"],
    "community_categories": {"valheim": CATEGORIES}, "categories": [], "has_nsfw_content": False,
})
print("submitted:", status, json.dumps(result)[:800])
