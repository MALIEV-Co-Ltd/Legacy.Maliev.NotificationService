"""Verify the review seal before any hosted SDK allocation."""
import hashlib
import json
import os
from pathlib import Path

root = Path(__file__).resolve().parent
manifest = json.loads((root / "source-manifest.json").read_text())
for entry in manifest["files"]:
    path = root / entry["path"]
    if path.parent != root or hashlib.sha256(path.read_bytes()).hexdigest() != entry["sha256"]:
        raise SystemExit("Source packet seal mismatch: " + entry["path"])
if os.environ.get("GITHUB_ACTIONS") == "true":
    installed = Path(os.environ["GITHUB_WORKSPACE"]) / ".github/workflows/notification62-hosted.yml"
    if installed.read_bytes() != (root / "notification62-hosted.yml").read_bytes():
        raise SystemExit("Installed hosted workflow differs from the reviewed packet")
print("Source packet sealed-file verification PASS; native validation remains separate")
