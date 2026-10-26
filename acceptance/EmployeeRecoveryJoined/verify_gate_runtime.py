"""Bounded Linux process controls; never starts an SDK, listener or container."""
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid

from run_hosted_join import SDK_GATE, group_members, proc_identity, quiesce


def probe(output, release):
    mode = "release" if release else "eof"
    run = uuid.uuid4().hex
    receipt = {"mode": mode, "run": run, "started": time.time(), "expires": time.time() + 30,
               "ports": [], "containers": [], "volumes": [], "persistentData": False,
               "gateReleased": False, "groupAbsent": False}
    path = output / (mode + "-custody.json")
    path.write_text(json.dumps(receipt, indent=2))
    process = None
    leader = None
    try:
        with (output / (mode + ".log")).open("wb") as log:
            process = subprocess.Popen(
                [sys.executable, "-B", "-c", SDK_GATE, sys.executable, "-B", "-c",
                 "import os; os.write(1, b'notification62-gate-sentinel\\n')"],
                env=dict(os.environ, N62_RUN_ID=run), stdin=subprocess.PIPE,
                stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            leader = proc_identity(process.pid)
            if leader is None or leader["pgrp"] != process.pid:
                leader = None
                raise RuntimeError("Real gate helper identity unavailable")
            receipt["leader"] = leader
            path.write_text(json.dumps(receipt, indent=2))
            if release:
                receipt["gateReleased"] = True
                process.stdin.write(b"1")
                process.stdin.flush()
            process.stdin.close()
            result = process.wait(timeout=10)
            receipt["exitCode"] = result
            if result != (0 if release else 125):
                raise RuntimeError("Real gate exit status mismatch")
        raw = (output / (mode + ".log")).read_bytes()
        if raw != (b"notification62-gate-sentinel\n" if release else b""):
            raise RuntimeError("Real gate target execution mismatch")
        receipt["sentinelVerified"] = True
    finally:
        try:
            if process is not None:
                if not process.stdin.closed:
                    process.stdin.close()
                if leader:
                    quiesce(leader, run, process)
                process.wait(timeout=5)
                if group_members(process.pid) or proc_identity(process.pid) is not None:
                    raise RuntimeError("Real gate helper absence is unverified")
                receipt["groupAbsent"] = True
        finally:
            path.write_text(json.dumps(receipt, indent=2))
    if receipt["groupAbsent"] is not True:
        raise RuntimeError("Real gate process custody incomplete")


if __name__ == "__main__":
    if os.name != "posix" or os.environ.get("GITHUB_ACTIONS") != "true":
        raise SystemExit("Actual gate controls require the hosted Linux job")
    output = Path(sys.argv[1]).resolve()
    output.mkdir(parents=True, exist_ok=False)
    probe(output, False)
    probe(output, True)
    print("Actual gate EOF/release controls and exact helper absence PASS")
