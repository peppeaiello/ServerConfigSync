"""Uploads a package to Thunderstore or Hexium (Valheim community).

Usage: python3 tools/publish.py <thunderstore|hexium> dist/ServerConfigSync-<version>.zip <team> [--validate-only]
API tokens are read from ~/.config/serverconfigsync/<site>-token (or TS_TOKEN / HEXIUM_TOKEN).
Both sites use the Thunderstore upload API: initiate an upload, PUT the parts, finish it, submit.
"""
import base64
import json
import os
import sys
import urllib.error
import urllib.request
import zipfile

SITES = {
    "thunderstore": {
        "api": "https://thunderstore.io",
        "env": "TS_TOKEN",
        "categories": ["mods", "server-side", "client-side", "utility", "ai-generated"],
    },
    "hexium": {
        "api": "https://hexium.gg",
        "env": "HEXIUM_TOKEN",
        "categories": ["Config", "Networking", "Tools", "Open Source"],
        "install_location": "both",
    },
}

site_name, zip_path, team = sys.argv[1], sys.argv[2], sys.argv[3]
validate_only = "--validate-only" in sys.argv
site = SITES[site_name]
API = site["api"]
token_file = os.path.expanduser(f"~/.config/serverconfigsync/{site_name}-token")
TOKEN = os.environ.get(site["env"]) or open(token_file).read().strip()


def call(method, url, body=None, auth=True, raw=None):
    data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
    req = urllib.request.Request(url, data=data, method=method)
    # Thunderstore's Cloudflare rejects the default urllib user agent (error 1010).
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
        print(f"HTTP {e.code} {method} {url.split('?')[0]}: {e.read().decode()[:2000]}")
        sys.exit(1)


if site_name == "thunderstore":
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
    url = part["url"]
    # Thunderstore hands out presigned storage URLs; Hexium's are its own API and need the token.
    _, _, headers = call("PUT", url, raw=chunk, auth=url.startswith(API))
    parts.append({"ETag": headers.get("ETag", ""), "PartNumber": part["part_number"]})
call("POST", f"{API}/api/experimental/usermedia/{uuid}/finish-upload/", {"parts": parts})
print("upload finished:", uuid)

status, result, _ = call("POST", f"{API}/api/experimental/submission/submit/", {
    "author_name": team,
    "upload_uuid": uuid,
    "communities": ["valheim"],
    "community_categories": {"valheim": site["categories"]},
    "categories": [],
    "has_nsfw_content": False,
})
print("submitted:", status, json.dumps(result)[:800])

if "install_location" in site:
    name = result["package_version"]["name"]
    status, result, _ = call("POST", f"{API}/api/mod/{team}/{name}/set-metadata",
                             {"install_location": site["install_location"], "categories": site["categories"]})
    print("metadata:", status, json.dumps(result)[:400])
