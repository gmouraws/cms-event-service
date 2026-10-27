"""Run the API or migrations with .env credentials on Windows and macOS."""
import argparse
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def environment():
    env = os.environ.copy()
    for line in (ROOT / ".env").read_text(encoding="utf-8").splitlines():
        if line and not line.startswith("#"):
            key, value = line.split("=", 1)
            env.setdefault(key, value)
    env.setdefault("ConnectionStrings__Writer", "Host=localhost;Port=" + env.get("POSTGRES_PORT", "5432")
                   + ";Database=cms;Username=cms;Password=" + env["POSTGRES_PASSWORD"])
    env.setdefault("ASPNETCORE_URLS", "http://localhost:8080")
    return env


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["run", "migrate"])
    args = parser.parse_args()
    command = ["dotnet", "run", "--project", "src/CmsEventService", "--no-launch-profile"]
    if args.action == "migrate":
        command += ["--", "--migrate"]
    raise SystemExit(subprocess.call(command, cwd=ROOT, env=environment()))
