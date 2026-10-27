"""Generate local development credentials without printing secrets."""
from pathlib import Path
import os
import secrets
import uuid

root = Path(__file__).resolve().parents[1]
target = root / ".env"
values = {
    "POSTGRES_PASSWORD": secrets.token_hex(32),
    "POSTGRES_PORT": "5432",
    "Auth__Cms__Username": "cms-" + secrets.token_hex(6),
    "Auth__Cms__Password": str(uuid.uuid4()),
    "Auth__Consumer__Username": "consumer-" + secrets.token_hex(4),
    "Auth__Consumer__Password": secrets.token_hex(32),
    "Auth__Admin__Username": "admin-" + secrets.token_hex(4),
    "Auth__Admin__Password": secrets.token_hex(32),
}
try:
    descriptor = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
except FileExistsError:
    raise SystemExit(".env already exists; keeping existing credentials.")
with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
    stream.write("\n".join(f"{key}={value}" for key, value in values.items()) + "\n")
print("Created .env with generated development credentials. Keep it private.")
