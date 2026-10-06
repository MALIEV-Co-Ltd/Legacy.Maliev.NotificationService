"""Preload the unchanged hosted test image; never retry application tests."""
import datetime
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

IMAGE = "postgres:18-alpine"
ATTEMPTS = 3
BACKOFF = (2, 5)
PULL_TIMEOUT = 90
INSPECT_TIMEOUT = 15
PROCESS_LEDGER = []


def transient_transport(output):
    if len(output) > 8192:
        return False
    lowered = output.lower()
    permanent = ("unauthorized", "denied", "manifest unknown", "not found", "invalid reference",
                 "too many requests", "toomanyrequests", "429", "no space", "out of memory")
    transient = ("connection reset by peer", "tls handshake timeout", "unexpected eof",
                 "i/o timeout", "connection timed out")
    return not any(x in lowered for x in permanent) and any(x in lowered for x in transient)


def process_identity(pid):
    directory = Path("/proc") / str(pid)
    try:
        fields = (directory / "stat").read_text().split(") ", 1)[1].split()
        ticks = int(fields[19])
        boot = next(int(row.split()[1]) for row in Path("/proc/stat").read_text().splitlines() if row.startswith("btime "))
        started = datetime.datetime.fromtimestamp(boot + ticks / os.sysconf("SC_CLK_TCK"), datetime.timezone.utc)
        return {"pid": pid, "kernelStartTicks": ticks, "actualStartUtc": started.isoformat(),
                "executable": str((directory / "exe").resolve(strict=True))}
    except (OSError, ValueError, StopIteration):
        return None


def execute(arguments, timeout):
    process = subprocess.Popen(["/usr/bin/docker", *arguments], stdin=subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    identity = process_identity(process.pid)
    record = {"purpose": "bounded PostgreSQL image preload", "command": arguments,
              "identity": identity, "pid": process.pid, "timeoutSeconds": timeout,
              "leaseExpiresUtc": (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(minutes=6)).isoformat(),
              "ports": [], "containers": [], "volumes": [], "persistentData": False}
    PROCESS_LEDGER.append(record)
    try:
        output, _ = process.communicate(timeout=timeout)
        return subprocess.CompletedProcess(arguments, process.returncode, output)
    finally:
        # Check exact kernel identity before signalling. Never wait indefinitely.
        try:
            if process.poll() is None and identity is not None and process_identity(process.pid) == identity:
                process.terminate()
                try:
                    process.communicate(timeout=5)
                except subprocess.TimeoutExpired:
                    if process.poll() is None and process_identity(process.pid) == identity:
                        process.kill()
                    process.communicate(timeout=5)
        except (OSError, subprocess.TimeoutExpired) as failure:
            record["cleanupFailure"] = type(failure).__name__
        finally:
            record["state"] = "verified exited" if process.poll() is not None else "cleanup uncertain; workflow lease applies"
            if process.poll() is not None and process.stdout is not None:
                process.stdout.close()


def verify_image(output):
    if len(output) > 8192:
        raise ValueError("Image metadata exceeded its bound")
    metadata = json.loads(output)
    if not isinstance(metadata, dict) or set(metadata) != {"digests", "environment", "os"}:
        raise ValueError("Image metadata shape is invalid")
    digests = metadata["digests"]
    if not isinstance(digests, list) or not all(isinstance(digest, str) for digest in digests) or not isinstance(metadata["environment"], list):
        raise ValueError("Image metadata types are invalid")
    if metadata["os"] != "linux" or "PG_MAJOR=18" not in metadata["environment"]:
        raise ValueError("Image is not the existing Linux PostgreSQL18 test image")
    valid = [digest for digest in digests if re.fullmatch(r"(?:docker\.io/library/)?postgres@sha256:[0-9a-f]{64}", digest)]
    if len(valid) != 1:
        raise ValueError("Exact PostgreSQL repository digest is unavailable")
    return valid[0]


def preload(runner=execute, sleeper=time.sleep):
    for attempt in range(1, ATTEMPTS + 1):
        pulled = runner(["pull", "--quiet", IMAGE], PULL_TIMEOUT)
        if pulled.returncode == 0:
            template = '{"digests":{{json .RepoDigests}},"environment":{{json .Config.Env}},"os":{{json .Os}}}'
            inspected = runner(["image", "inspect", "--format", template, IMAGE], INSPECT_TIMEOUT)
            if inspected.returncode != 0:
                raise ValueError("Preloaded image inspection failed")
            return {"image": IMAGE, "digest": verify_image(inspected.stdout), "attempts": attempt,
                    "maxAttempts": ATTEMPTS, "backoffSeconds": list(BACKOFF), "state": "ready"}
        if attempt == ATTEMPTS or not transient_transport(pulled.stdout):
            raise ValueError("PostgreSQL image preload failed closed")
        sleeper(BACKOFF[attempt - 1])
    raise AssertionError("Unreachable preload state")


def main():
    evidence = Path("runner-results/postgres-preload.json")
    result = {"image": IMAGE, "state": "failed", "run": os.environ.get("GITHUB_RUN_ID"),
              "runAttempt": os.environ.get("GITHUB_RUN_ATTEMPT"), "processLedger": PROCESS_LEDGER}
    try:
        if sys.platform != "linux" or os.environ.get("GITHUB_ACTIONS") != "true" or len(sys.argv) != 1:
            raise ValueError("Preload is restricted to the fixed hosted Linux workflow")
        result.update(preload())
        print("PostgreSQL18 image ready; exact digest retained in validation evidence")
        return 0
    except (OSError, ValueError, subprocess.TimeoutExpired):
        print("PostgreSQL image preload failed closed; no tests were retried", file=sys.stderr)
        return 1
    finally:
        evidence.parent.mkdir(parents=True, exist_ok=True)
        evidence.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
