"""Executable synthetic request examples; credentials never appear in output."""
import base64
import json
import os
import urllib.error
import urllib.request
import uuid
from dev import environment

env = environment()
base = os.environ.get("CMS_BASE_URL", "http://localhost:8080").rstrip("/")
entity_id = "smoke-" + uuid.uuid4().hex


def request(role, method, path, data=None, expected=200):
    headers = {}
    if role:
        credentials = env[f"Auth__{role}__Username"] + ":" + env[f"Auth__{role}__Password"]
        headers["Authorization"] = "Basic " + base64.b64encode(credentials.encode()).decode()
    body = None if data is None else json.dumps(data).encode()
    if body is not None:
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(base + path, data=body, headers=headers, method=method)
    try:
        response = urllib.request.urlopen(req, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        assert response.status == expected, f"{method} {path}: expected {expected}, got {response.status}"
        content = response.read()
    print(f"PASS {method} {path}: {expected}")
    return json.loads(content) if content else None


def event(kind, version=None, second=1, title="Synthetic example"):
    value = {"type": kind, "id": entity_id, "timestamp": f"2024-01-01T00:00:{second:02d}Z"}
    if version is not None:
        value.update(version=version, payload={"title": title})
    return value


request(None, "GET", "/health/ready")
request(None, "GET", "/entities", expected=401)
request("Consumer", "POST", "/cms/events", [event("publish", 1)], 403)
request("Cms", "POST", "/cms/events", [event("publish", 1)])
request("Consumer", "GET", f"/entities/{entity_id}")
request("Cms", "POST", "/cms/events", [event("unPublish", 2, 2, "Unpublished revision")])
request("Consumer", "GET", f"/entities/{entity_id}", expected=404)
row = request("Admin", "GET", f"/entities/{entity_id}")
assert row["version"] == 2 and row["payload"]["title"] == "Unpublished revision"
request("Admin", "POST", f"/entities/{entity_id}/disable", expected=204)
request("Cms", "POST", "/cms/events", [event("publish", 3, 3)])
request("Consumer", "GET", f"/entities/{entity_id}", expected=404)
request("Consumer", "GET", "/entities?limit=10&offset=0")
request("Consumer", "GET", "/openapi/v1.json")
request("Cms", "POST", "/cms/events", [event("delete", second=4)])
request("Admin", "GET", f"/entities/{entity_id}", expected=404)
print("Smoke flow passed; synthetic entity data was deleted.")
