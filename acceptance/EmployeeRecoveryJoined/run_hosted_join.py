"""Source-pinned, Linux-hosted supervisor. Never allocates SDK resources by default."""
import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time
import uuid

PACKET = Path(__file__).resolve().parent
MIN_AVAILABLE_KIB = 8 * 1024 * 1024
LABEL = "maliev.validation.notification62.run"


def command(args, **kwargs):
    return subprocess.run(args, capture_output=True, text=True, timeout=30, check=True, **kwargs).stdout.strip()


def verify_sources(roots, heads):
    bindings = json.loads((PACKET / "source-bindings.json").read_text())
    for name, root in roots.items():
        expected = heads[name]
        if not re.fullmatch(r"[0-9a-f]{40}", expected):
            raise ValueError("Every hosted source head must be an explicit immutable SHA")
        candidate = bindings.get("candidateHeads", {}).get(name)
        if candidate and expected != candidate["head"]:
            raise ValueError("Producer candidate differs from the reviewed published head: " + name)
        if command(["git", "-C", str(root), "rev-parse", "HEAD"]) != expected:
            raise ValueError("Hosted source head mismatch: " + name)
        if candidate and command(["git", "-C", str(root), "rev-parse", "HEAD^{tree}"]) != candidate["tree"]:
            raise ValueError("Published producer tree mismatch: " + name)
        if command(["git", "-C", str(root), "status", "--porcelain", "--untracked-files=no"]):
            raise ValueError("Hosted source checkout is dirty: " + name)
        for item in bindings["files"]:
            if item["repo"] == name:
                command(["git", "-C", str(root), "ls-files", "--error-unmatch", "--", item["path"]])
                if hashlib.sha256((root / item["path"]).read_bytes()).hexdigest() != item["sha256"]:
                    raise ValueError("Reviewed source file mismatch: " + name + "/" + item["path"])
    return {name: {"head": heads[name], "tree": command(["git", "-C", str(root), "rev-parse", "HEAD^{tree}"])}
            for name, root in roots.items()}


def proc_identity(pid):
    try:
        raw = Path(f"/proc/{pid}/stat").read_text()
        fields = raw[raw.rfind(")") + 2:].split()
        try:
            executable = str(Path(f"/proc/{pid}/exe").readlink())
        except FileNotFoundError:
            if fields[0] != "Z":
                return None
            executable = "<exited>"
        return {"pid": int(pid), "pgrp": int(fields[2]), "birthTicks": int(fields[19]), "executable": executable}
    except (FileNotFoundError, ProcessLookupError, PermissionError):
        return None


def group_members(pgrp):
    found = []
    for directory in Path("/proc").iterdir():
        if directory.name.isdecimal():
            identity = proc_identity(directory.name)
            if identity and identity["pgrp"] == pgrp:
                found.append(identity)
    return found


def verify_owned(member, leader, run):
    if member["pgrp"] != leader["pgrp"] or member["birthTicks"] < leader["birthTicks"]:
        raise RuntimeError("Process ownership identity mismatch")
    raw = Path(f"/proc/{member['pid']}/environ").read_bytes().split(b"\0")
    if ("N62_RUN_ID=" + run).encode() not in raw:
        raise RuntimeError("Process lacks the exact run ownership marker")


def signal_exact(member, leader, run, sig):
    current = proc_identity(member["pid"])
    if current is None:
        return
    if current != member:
        raise RuntimeError("Process identity changed; preserve PID")
    verify_owned(current, leader, run)
    os.kill(current["pid"], sig)


def quiesce(leader, run, process=None):
    members = group_members(leader["pgrp"])
    for member in members:
        if member["executable"] != "<exited>":
            signal_exact(member, leader, run, signal.SIGTERM)
    deadline = time.monotonic() + 10
    while group_members(leader["pgrp"]) and time.monotonic() < deadline:
        if process is not None:
            process.poll()  # Reap the owned direct child; never hide zombie group members.
        time.sleep(0.2)
    for member in group_members(leader["pgrp"]):
        if member["executable"] != "<exited>":
            signal_exact(member, leader, run, signal.SIGKILL)
    deadline = time.monotonic() + 5
    while group_members(leader["pgrp"]) and time.monotonic() < deadline:
        if process is not None:
            process.poll()
        time.sleep(0.2)
    if group_members(leader["pgrp"]):
        raise RuntimeError("Owned native group is not quiescent")


def admit():
    fields = dict(line.split(":", 1) for line in Path("/proc/meminfo").read_text().splitlines())
    available = int(fields["MemAvailable"].split()[0])
    required = max(MIN_AVAILABLE_KIB, int(os.environ.get("N62_REQUIRED_MEM_KIB", MIN_AVAILABLE_KIB)))
    if available < required:
        raise RuntimeError(f"Resource admission failed: MemAvailable={available}KiB required={required}KiB")
    for directory in Path("/proc").iterdir():
        if directory.name.isdecimal():
            identity = proc_identity(directory.name)
            if identity and Path(identity["executable"]).name in ("dotnet", "testhost"):
                raise RuntimeError("Existing native SDK/test worker prevents admission; preserve it")
    return {"availableKiB": available, "requiredKiB": required}


def validate_container(value, run, started):
    labels = value["Config"].get("Labels", {})
    if labels.get(LABEL) != run or not re.fullmatch(r"[0-9a-f]{32}", labels.get("maliev.validation.notification62.fixture", "")):
        raise RuntimeError("Container ownership labels mismatch")
    if value["Config"]["Image"] not in ("postgres:18-alpine", "redis:7-alpine"):
        raise RuntimeError("Container executable/image identity mismatch")
    created = dt.datetime.fromisoformat(value["Created"].replace("Z", "+00:00"))
    if created < started or created > dt.datetime.now(dt.timezone.utc) + dt.timedelta(seconds=5):
        raise RuntimeError("Container creation evidence is outside this run")
    if any(mount["Type"] != "tmpfs" for mount in value.get("Mounts", [])):
        raise RuntimeError("Container has a potentially persistent mount; preserve it")
    for bindings in (value["HostConfig"].get("PortBindings") or {}).values():
        if any(binding["HostIp"] != "127.0.0.1" for binding in bindings):
            raise RuntimeError("Container has a non-loopback port binding")
    limits = value["HostConfig"]
    if not 0 < limits.get("Memory", 0) <= 1024 * 1024 * 1024 or not 0 < limits.get("NanoCpus", 0) <= 1_000_000_000:
        raise RuntimeError("Container resource limits mismatch")
    return {"id": value["Id"], "created": value["Created"], "image": value["Config"]["Image"],
            "labels": labels, "mountTypes": [mount["Type"] for mount in value.get("Mounts", [])],
            "ports": limits.get("PortBindings"), "memory": limits["Memory"], "nanoCpus": limits["NanoCpus"]}


class ContainerCleanupError(RuntimeError):
    def __init__(self, receipts, failures):
        super().__init__("Some containers were preserved or failed cleanup; exact receipts retained")
        self.receipts = receipts
        self.failures = failures


def cleanup_containers(run, started):
    receipts = []
    failures = []
    ids = command(["docker", "container", "ls", "-a", "--filter", "label=" + LABEL + "=" + run,
                   "--format", "{{.ID}}", "--no-trunc"]).splitlines()
    for container in ids:
        try:
            value = json.loads(command(["docker", "container", "inspect", container]))[0]
            receipt = validate_container(value, run, started)
            command(["docker", "container", "stop", "--time", "10", container])
            # Re-observe exact immutable ID, labels, creation and mounts before removal.
            validate_container(json.loads(command(["docker", "container", "inspect", container]))[0], run, started)
            command(["docker", "container", "rm", container])
            remaining = command(["docker", "container", "ls", "-a", "--no-trunc", "--format", "{{.ID}}"]).splitlines()
            if container in remaining:
                raise RuntimeError("Owned container remains after exact removal")
            receipt["absent"] = True
            receipts.append(receipt)
        except Exception as exception:
            failures.append({"id": container, "failureType": type(exception).__name__, "preservedOrRemaining": True})
    if failures:
        raise ContainerCleanupError(receipts, failures)
    return receipts


def run_native(args, env, output, stage, run, deadline=None):
    if deadline is not None and time.monotonic() >= deadline:
        raise TimeoutError("Overall hosted native lease expired before resource admission")
    admission = admit()
    leader = None
    process = None
    receipt = {"stage": stage, "admission": admission, "command": args}
    try:
        with (output / (stage + ".log")).open("w") as log:
            process = subprocess.Popen(args, env=env, cwd=PACKET, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            leader = proc_identity(process.pid)
            if leader is None or leader["pgrp"] != process.pid:
                raise RuntimeError("Native leader custody unavailable")
            receipt["leader"] = leader
            (output / (stage + "-custody.json")).write_text(json.dumps(receipt, indent=2))
            try:
                remaining = 900 if deadline is None else min(900, max(1, deadline - time.monotonic()))
                result = process.wait(timeout=remaining)
                if result != 0:
                    raise RuntimeError(f"Native {stage} failed with exit {result}")
            finally:
                quiesce(leader, run, process)
                process.wait(timeout=5)
                receipt["groupAbsent"] = not group_members(leader["pgrp"])
    finally:
        if leader is not None and group_members(leader["pgrp"]):
            quiesce(leader, run, process)
        (output / (stage + "-custody.json")).write_text(json.dumps(receipt, indent=2))


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "--cleanup-only":
        output = Path(sys.argv[2]).resolve()
        owner = output / "run-owner.json"
        if not owner.exists():
            print("No native allocation was recorded")
            return
        if os.environ.get("GITHUB_ACTIONS") != "true" or os.name != "posix":
            raise RuntimeError("Cleanup receipts require the original hosted Linux job")
        record = json.loads(owner.read_text())
        try:
            for path in output.glob("*-custody.json"):
                leader = json.loads(path.read_text()).get("leader")
                if leader:
                    quiesce(leader, record["run"])
            record["containers"] = cleanup_containers(record["run"], dt.datetime.fromisoformat(record["started"]))
            record["result"] = "PASS"
        except Exception as exception:
            record["result"] = "FAIL"
            record["failureType"] = type(exception).__name__
            if isinstance(exception, ContainerCleanupError):
                record["containers"] = exception.receipts
                record["remaining"] = exception.failures
            raise
        finally:
            (output / "cleanup-final.json").write_text(json.dumps(record, indent=2))
        return
    parser = argparse.ArgumentParser()
    for name in ("auth", "intranet", "notification", "defaults", "contracts"):
        parser.add_argument("--" + name + "-root", required=True, type=Path)
        parser.add_argument("--" + name + "-head", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args()
    names = ("auth", "intranet", "notification", "defaults", "contracts")
    roots = {name: getattr(args, name + "_root").resolve() for name in names}
    heads = {name: getattr(args, name + "_head") for name in names}
    if heads["defaults"] != "4517cf16f5f1159318e184969732d46eae4a8308" or heads["contracts"] != "78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7":
        raise ValueError("Joined shared dependency pins must match the reviewed source packet")
    pins = verify_sources(roots, heads)
    if roots["defaults"].parent != roots["contracts"].parent:
        raise ValueError("Shared dependencies must occupy the same isolated workspace")
    args.output.mkdir(parents=True, exist_ok=False)
    (args.output / "tested-source.json").write_text(json.dumps(pins, indent=2))
    if not args.execute:
        print("Exact sources verified; native execution not requested")
        return
    if os.name != "posix" or os.environ.get("GITHUB_ACTIONS") != "true":
        raise RuntimeError("Native execution is restricted to the admitted hosted Linux workflow")
    def interrupted(_signal, _frame):
        raise InterruptedError("Hosted run interrupted; owned finally cleanup is required")
    signal.signal(signal.SIGTERM, interrupted)
    run = uuid.uuid4().hex
    started = dt.datetime.now(dt.timezone.utc)
    env = dict(os.environ, N62_RUN_ID=run, N62_RESOURCE_DIRECTORY=str(args.output.resolve() / "resources"),
               RECOVERY_AUTH_ROOT=str(roots["auth"]), RECOVERY_INTRANET_ROOT=str(roots["intranet"]),
               RECOVERY_NOTIFICATION_ROOT=str(roots["notification"]), TESTCONTAINERS_RYUK_DISABLED="true",
               RecoveryAuthRoot=str(roots["auth"]), RecoveryIntranetRoot=str(roots["intranet"]),
               RecoveryNotificationRoot=str(roots["notification"]), MalievWorkspaceRoot=str(roots["defaults"].parent),
               UseLocalMalievDependencies="true", UseSharedCompilation="false",
               DOTNET_PROCESSOR_COUNT="2", MSBUILDDISABLENODEREUSE="1", DOTNET_CLI_TELEMETRY_OPTOUT="1",
               DOTNET_GCHeapHardLimit="0x100000000")
    properties = ["-p:RecoveryAuthRoot=" + str(roots["auth"]), "-p:RecoveryIntranetRoot=" + str(roots["intranet"]),
                  "-p:RecoveryNotificationRoot=" + str(roots["notification"]),
                  "-p:MalievWorkspaceRoot=" + str(roots["defaults"].parent), "-p:UseLocalMalievDependencies=true",
                  "-p:UseSharedCompilation=false", "-m:1", "-nr:false"]
    cleanup = {"run": run, "started": started.isoformat(), "expires": (started + dt.timedelta(minutes=50)).isoformat()}
    (args.output / "run-owner.json").write_text(json.dumps(cleanup))
    deadline = time.monotonic() + 45 * 60
    try:
        for stage, arguments in (
            ("restore", ["restore"]),
            ("build", ["build", "-c", "Release", "--no-restore", "-warnaserror"]),
            ("test", ["test", "-c", "Release", "--no-build", "--logger", "trx", "--results-directory", str(args.output.resolve() / "trx")]),
        ):
            if time.monotonic() >= deadline:
                raise TimeoutError("Overall hosted native lease expired")
            run_native(["dotnet", *arguments, "JoinedRecovery.AcceptanceTests.csproj", *properties], env, args.output, stage, run, deadline)
        run_native(["dotnet", "format", "JoinedRecovery.AcceptanceTests.csproj", "--verify-no-changes", "--no-restore",
                    "--include", "JoinedRecoveryTests.cs", "RecoveryJoinFixture.cs", "HostPoolCleanupRegressionTests.cs",
                    "HostDisposalGate.cs", "HostDisposalFailureRegressionTests.cs"], env, args.output, "format", run, deadline)
        run_native(["dotnet", "list", "JoinedRecovery.AcceptanceTests.csproj", "package", "--vulnerable",
                    "--include-transitive", "--no-restore", "--format", "json"], env, args.output, "package-audit", run, deadline)
        audit = json.loads((args.output / "package-audit.log").read_text())
        if not isinstance(audit.get("projects"), list) or not audit["projects"]:
            raise RuntimeError("Hosted package audit output is unavailable or malformed")
        for project in audit.get("projects", []):
            for framework in project.get("frameworks", []):
                for category in ("topLevelPackages", "transitivePackages"):
                    if any(package.get("vulnerabilities") for package in framework.get(category, [])):
                        raise RuntimeError("Hosted package audit reports vulnerabilities")
    finally:
        try:
            for receipt in args.output.glob("*-custody.json"):
                leader = json.loads(receipt.read_text()).get("leader")
                if leader and group_members(leader["pgrp"]):
                    raise RuntimeError("Native clients remain; preserve containers until exact process cleanup succeeds")
            cleanup["containers"] = cleanup_containers(run, started)
            cleanup["result"] = "PASS"
        except Exception as exception:
            cleanup["result"] = "FAIL"
            cleanup["failureType"] = type(exception).__name__
            if isinstance(exception, ContainerCleanupError):
                cleanup["containers"] = exception.receipts
                cleanup["remaining"] = exception.failures
            raise
        finally:
            (args.output / "cleanup.json").write_text(json.dumps(cleanup, indent=2))


if __name__ == "__main__":
    main()
