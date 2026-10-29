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

# V20 binds the exact reviewed V19 preimage; a hash-only reseal is insufficient.
V19_ANCHORS = {'v19-source-manifest.json': '9093b2bfc93c5bc3fde1d2d86ff8d4e578bc410983271270aeeaf19e5288a7a8', 'v19-notification62-hosted.yml': '6ec629c83585ff815adb514eb25b0ae892245b23ab8ec6554452eaede9cacf9c', 'v19-verify_packet.py.txt': 'c94443baea808b430b0840e1c45f1287407d1d1dc7d50ee3ddc35c9d4715ec54', 'v19-test_run_hosted_join.py.txt': '7f54961720fd5c1a2943e86956c12710e3085d400f902e581ae63bf29d8a5427'}
for name, expected in V19_ANCHORS.items():
    if hashlib.sha256((root / name).read_bytes()).hexdigest() != expected:
        raise SystemExit("V19 predecessor archive mismatch: " + name)
previous = json.loads((root / "v19-source-manifest.json").read_bytes())
current = manifest
if current.get("version") != "V20 artifact transport action successor" or current.get("supersedesSourceManifestSha256") != V19_ANCHORS["v19-source-manifest.json"]:
    raise SystemExit("Exact V19 successor metadata required")
excluded = {"version", "supersedesSourceManifestSha256", "dispatchHold", "files"}
if {k:v for k,v in current.items() if k not in excluded} != {k:v for k,v in previous.items() if k not in excluded}:
    raise SystemExit("V19 source policy or identity changed")
if current.get("dispatchHold") != "V20 joined native validation requires a new explicit scoped authorization; historical V19 evidence remains historical.":
    raise SystemExit("V20 dispatch hold required")
before = (root / "v19-notification62-hosted.yml").read_bytes()
old = b"actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02"
new = b"actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9"
if before.count(old) != 1 or new in before or (root / "notification62-hosted.yml").read_bytes() != before.replace(old, new):
    raise SystemExit("Exact reviewed artifact action delta required")
oldrows = {e["path"]: e["sha256"] for e in previous["files"]}
newrows = {e["path"]: e["sha256"] for e in current["files"]}
if len(oldrows) != len(previous["files"]) or len(newrows) != len(current["files"]):
    raise SystemExit("Duplicate packet seal refused")
changed = {"notification62-hosted.yml", "verify_packet.py", "test_run_hosted_join.py"}
if set(newrows) != set(oldrows) | set(V19_ANCHORS) or any(newrows[k] != v for k,v in oldrows.items() if k not in changed):
    raise SystemExit("Unreviewed V19 packet seal or roster change")
if newrows["test_run_hosted_join.py"] != "16b0a59e6745cf7cf9f0b0228525c88a78cd66429a1df6db820d1354b96861b2":
    raise SystemExit("Exact V20 packet controls required")
for name, expected in V19_ANCHORS.items():
    if newrows[name] != expected:
        raise SystemExit("V19 predecessor seal mismatch")

print("Source packet sealed-file verification PASS; native validation remains separate")
