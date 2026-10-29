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

V19_ANCHORS = {'v19-source-manifest.json': '9093b2bfc93c5bc3fde1d2d86ff8d4e578bc410983271270aeeaf19e5288a7a8', 'v19-notification62-hosted.yml': '6ec629c83585ff815adb514eb25b0ae892245b23ab8ec6554452eaede9cacf9c', 'v19-verify_packet.py.txt': 'c94443baea808b430b0840e1c45f1287407d1d1dc7d50ee3ddc35c9d4715ec54', 'v19-test_run_hosted_join.py.txt': '7f54961720fd5c1a2943e86956c12710e3085d400f902e581ae63bf29d8a5427'}
for name, expected in V19_ANCHORS.items():
    if hashlib.sha256((root / name).read_bytes()).hexdigest() != expected:
        raise SystemExit("V19 predecessor archive mismatch: " + name)

V20_ANCHORS = {
    "v20-source-manifest.json": "9fe8a568f2b605f4a7fd35a016bb729a5aeb8e6719b9fd6f3a6cdc8fc5a0db80",
    "v20-run_hosted_join.py.txt": "e3682950d71b796bfcf1795c1c54a8fc1f0d8f8df16985ae8693570c4cf5671e",
    "v20-verify_packet.py.txt": "055331d9ab6c6863ce87dbb6345cd8107fe888dda5cbded3290076d7dbc5ac92",
    "v20-test_run_hosted_join.py.txt": "16b0a59e6745cf7cf9f0b0228525c88a78cd66429a1df6db820d1354b96861b2",
    "v20-notification62-hosted.yml": "ff3dcad38a8fc42a2c1870c755c58f72d4a6fcf49f1d424e272cfb5b2485b4b9"}
for name, expected in V20_ANCHORS.items():
    if hashlib.sha256((root / name).read_bytes()).hexdigest() != expected:
        raise SystemExit("V20 predecessor archive mismatch: " + name)

# Replay every V20 preimage/seal condition against its immutable archived bytes.
previous = json.loads((root / "v19-source-manifest.json").read_bytes())
current = json.loads((root / "v20-source-manifest.json").read_bytes())
archived = {"run_hosted_join.py": "v20-run_hosted_join.py.txt", "verify_packet.py": "v20-verify_packet.py.txt",
            "test_run_hosted_join.py": "v20-test_run_hosted_join.py.txt", "notification62-hosted.yml": "v20-notification62-hosted.yml"}
for entry in current["files"]:
    path = root / archived.get(entry["path"], entry["path"])
    if path.parent != root or hashlib.sha256(path.read_bytes()).hexdigest() != entry["sha256"]:
        raise SystemExit("Unreviewed V19 packet seal or roster change; V20 archived packet seal mismatch: " + entry["path"])
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
if before.count(old) != 1 or new in before or (root / "v20-notification62-hosted.yml").read_bytes() != before.replace(old, new):
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

# Only the bounded diagnostic hook, its controls and this verifier may differ from V20.
previous, current = current, manifest
if current.get("version") != "V21 bounded Client lock diagnostic successor" or current.get("supersedesSourceManifestSha256") != V20_ANCHORS["v20-source-manifest.json"]:
    raise SystemExit("Exact V20 diagnostic successor metadata required")
if {k:v for k,v in current.items() if k not in excluded} != {k:v for k,v in previous.items() if k not in excluded}:
    raise SystemExit("V19 source policy or identity changed")
if current.get("dispatchHold") != "V21 diagnostic acquisition requires new explicit scoped authorization; source drift remains rejected and historical native evidence remains historical.":
    raise SystemExit("V21 diagnostic dispatch hold required")
oldrows = {e["path"]: e["sha256"] for e in previous["files"]}
newrows = {e["path"]: e["sha256"] for e in current["files"]}
if len(oldrows) != len(previous["files"]) or len(newrows) != len(current["files"]):
    raise SystemExit("Duplicate packet seal refused")
changed = {"run_hosted_join.py", "verify_packet.py", "test_run_hosted_join.py"}
if set(newrows) != set(oldrows) | set(V20_ANCHORS) or any(newrows[k] != v for k,v in oldrows.items() if k not in changed):
    raise SystemExit("Unreviewed V19 packet seal or roster change")
# Preserve the frozen rejection spelling used by V20's unchanged negative controls.
if newrows["test_run_hosted_join.py"] != "75a5f03909c39df93cf56defaa04a57ae3d1d225403b51471e50adb5bda786ab":
    raise SystemExit("Exact V20 packet controls required; reviewed V21 controls also required")
if newrows["run_hosted_join.py"] != "5815a5c0181992bf82c99db48f4b0a8dbb4d35c8e93d8c2f5628ec8ca4469de1":
    raise SystemExit("Exact reviewed V21 diagnostic supervisor required")
for name, expected in V20_ANCHORS.items():
    if newrows[name] != expected:
        raise SystemExit("V20 predecessor seal mismatch")
if (root / "notification62-hosted.yml").read_bytes() != (root / "v20-notification62-hosted.yml").read_bytes():
    raise SystemExit("V21 must preserve the exact V20 workflow")

print("Source packet sealed-file verification PASS; diagnostic acquisition is not native acceptance")
