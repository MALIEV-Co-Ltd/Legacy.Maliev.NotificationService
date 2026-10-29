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
from causal_baseline import historical_model, verify_baseline, verify_restored

PACKET = Path(__file__).resolve().parent
MIN_AVAILABLE_KIB = 8 * 1024 * 1024
LABEL = "maliev.validation.notification62.run"
SDK_GATE = "import os,sys\nif os.read(0,1) != b'1': sys.exit(125)\nos.execvpe(sys.argv[1],sys.argv[1:],os.environ)\n"
BFF_LOCK = "Legacy.Maliev.Intranet.Bff/packages.lock.json"
BFF_LOCK_BEFORE = "9db1aa34e5170a7912923961637c0a4a9b0fa11bd6164169ca3111e820dfb885"
BFF_LOCK_AFTER = "20f61f6207e89e260dd41d44e246ee9d052b237a97a63cc7fef24d30c6117cd6"


def command(args, **kwargs):
    return subprocess.run(args, capture_output=True, text=True, timeout=30, check=True, **kwargs).stdout.strip()


def validate_nuget_lock(raw):
    if len(raw) > 1024 * 1024:
        raise ValueError("Composed lock exceeds the reviewed byte bound")
    value = json.loads(raw)
    if set(value) != {"version", "dependencies"} or value["version"] != 1 or set(value["dependencies"]) != {"net10.0"}:
        raise ValueError("Unexpected NuGet lock schema")
    packages = value["dependencies"]["net10.0"]
    if not isinstance(packages, dict) or len(packages) > 512:
        raise ValueError("Unexpected NuGet package inventory")
    def package_id(name):
        return isinstance(name, str) and re.fullmatch(r"[A-Za-z0-9_.-]{1,100}", name)
    def version(text):
        return isinstance(text, str) and re.fullmatch(r"[A-Za-z0-9.+*(),\[\] -]{1,100}", text)
    for name, item in packages.items():
        if (not package_id(name) or not isinstance(item, dict)
                or set(item) - {"type", "requested", "resolved", "contentHash", "dependencies"}
                or item.get("type") not in ("Direct", "Transitive", "Project")):
            raise ValueError("Unexpected NuGet package metadata")
        for key in ("requested", "resolved"):
            if key in item and not version(item[key]):
                raise ValueError("Unexpected NuGet package version")
        if "contentHash" in item and (not isinstance(item["contentHash"], str)
                or not re.fullmatch(r"[A-Za-z0-9+/]{86}==", item["contentHash"])):
            raise ValueError("Unexpected NuGet content hash")
        dependencies = item.get("dependencies", {})
        if (not isinstance(dependencies, dict) or len(dependencies) > 512
                or any(not package_id(key) or not version(val) for key, val in dependencies.items())):
            raise ValueError("Unexpected NuGet dependency metadata")
    return packages


def capture_composed_lock(before, after, output):
    if (hashlib.sha256(before).hexdigest() != BFF_LOCK_BEFORE
            or hashlib.sha256(after).hexdigest() != BFF_LOCK_AFTER):
        raise ValueError("Composed lock differs from the observed exact hash pair")
    old, new = validate_nuget_lock(before), validate_nuget_lock(after)
    delta = []
    for name in sorted(set(old) | set(new)):
        previous, current = old.get(name, {}), new.get(name, {})
        if previous != current:
            delta.append({"package": name,
                          "before": {key: previous[key] for key in ("type", "requested", "resolved") if key in previous},
                          "after": {key: current[key] for key in ("type", "requested", "resolved") if key in current},
                          "dependenciesChanged": previous.get("dependencies") != current.get("dependencies"),
                          "contentHashChanged": previous.get("contentHash") != current.get("contentHash")})
    (output / "composed-bff.packages.lock.json").write_bytes(after)
    (output / "composed-lock-delta.json").write_text(json.dumps({
        "path": BFF_LOCK, "beforeSha256": BFF_LOCK_BEFORE, "afterSha256": BFF_LOCK_AFTER,
        "packages": delta}, indent=2))


def verify_composed_lock():
    raw = (PACKET / "composed-bff.packages.lock.json").read_bytes()
    if hashlib.sha256(raw).hexdigest() != BFF_LOCK_AFTER:
        raise ValueError("Reviewed composed BFF lock changed")
    validate_nuget_lock(raw)


def record_source_changes(root, output):
    paths = [path for path in command(["git", "-C", str(root), "diff", "HEAD", "--name-only", "-z"]).split("\0") if path]
    if len(paths) > 64:
        raise ValueError("Source-change diagnostic exceeds the reviewed path bound")
    rows = []
    composed = None
    for relative in paths:
        path = (root / relative).resolve()
        if not path.is_relative_to(root.resolve()):
            raise ValueError("Source-change diagnostic escapes the checkout")
        size = int(command(["git", "-C", str(root), "cat-file", "-s", "HEAD:" + relative]))
        if size > 1024 * 1024 or (path.exists() and path.stat().st_size > 1024 * 1024):
            raise ValueError("Source-change diagnostic exceeds the reviewed byte bound")
        before = subprocess.run(["git", "-C", str(root), "show", "HEAD:" + relative],
                                capture_output=True, check=True, timeout=30).stdout
        after = path.read_bytes() if path.exists() else None
        if root.name == "Legacy.Maliev.Intranet" and relative == BFF_LOCK and after is not None:
            composed = (before, after)
        rows.append({"path": relative, "beforeSize": len(before), "beforeSha256": hashlib.sha256(before).hexdigest(),
                     "afterSize": len(after) if after is not None else None,
                     "afterSha256": hashlib.sha256(after).hexdigest() if after is not None else None})
    output.write_text(json.dumps({"repository": root.name, "trackedChanges": rows}, indent=2))
    if composed is not None:
        capture_composed_lock(*composed, output.parent)


def verify_sources(roots, heads, diagnostics=None):
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
            if diagnostics is not None:
                record_source_changes(root, diagnostics)
            raise ValueError("Hosted source checkout is dirty: " + name)
        for item in bindings["files"]:
            if item["repo"] == name:
                command(["git", "-C", str(root), "ls-files", "--error-unmatch", "--", item["path"]])
                raw = (root / item["path"]).read_bytes()
                if "checkoutEol" in item:
                    if item["checkoutEol"] != "crlf" or name != "intranet" or not item["path"].endswith(".cs"):
                        raise ValueError("Reviewed source checkout rule is invalid")
                    canonical = raw.replace(b"\r\n", b"\n")
                    if b"\r" in canonical or raw != canonical.replace(b"\n", b"\r\n"):
                        raise ValueError("Reviewed source checkout line endings mismatch: " + item["path"])
                    raw = canonical
                if hashlib.sha256(raw).hexdigest() != item["sha256"]:
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
    memory, cpu = ((128 * 1024 * 1024, 500_000_000) if value["Config"]["Image"] == "redis:7-alpine"
                   else (1024 * 1024 * 1024, 1_000_000_000))
    if not 0 < limits.get("Memory", 0) <= memory or not 0 < limits.get("NanoCpus", 0) <= cpu:
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


def run_native(args, env, output, stage, run, deadline=None, expected_exit=0):
    if deadline is not None and time.monotonic() >= deadline:
        raise TimeoutError("Overall hosted native lease expired before resource admission")
    admission = admit()
    leader = None
    process = None
    released = False
    receipt = {"stage": stage, "admission": admission, "command": args, "gateReleased": False,
               "groupAbsent": False}
    owner_path = output / "run-owner.json"
    owner = json.loads(owner_path.read_text())
    owner["stages"].append(stage)
    owner_path.write_text(json.dumps(owner))
    custody = output / (stage + "-custody.json")
    custody.write_text(json.dumps(receipt, indent=2))
    try:
        with (output / (stage + ".log")).open("w") as log:
            # The direct child cannot exec the SDK until its identity is durably recorded.
            process = subprocess.Popen([sys.executable, "-c", SDK_GATE, *args], env=env, cwd=PACKET,
                                       stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            leader = proc_identity(process.pid)
            if leader is None or leader["pgrp"] != process.pid:
                leader = None
                raise RuntimeError("Native leader custody unavailable")
            receipt["leader"] = leader
            custody.write_text(json.dumps(receipt, indent=2))
            # Mark release before the write: a partial write must use full native cleanup.
            released = True
            receipt["gateReleased"] = True
            process.stdin.write(b"1")
            process.stdin.flush()
            process.stdin.close()
            try:
                remaining = 900 if deadline is None else min(900, max(1, deadline - time.monotonic()))
                result = process.wait(timeout=remaining)
                receipt["exitCode"] = result
                receipt["expectedExitCode"] = expected_exit
                if result != expected_exit:
                    raise RuntimeError(f"Native {stage} failed with exit {result}")
            finally:
                quiesce(leader, run, process)
                process.wait(timeout=5)
                receipt["groupAbsent"] = not group_members(leader["pgrp"])
    finally:
        try:
            if process is not None and not released:
                # EOF is graceful cancellation of the unopened gate, not a PID-based kill.
                process.stdin.close()
                process.wait(timeout=5)
                receipt["groupAbsent"] = not group_members(process.pid)
            elif leader is not None:
                quiesce(leader, run, process)
                process.wait(timeout=5)
                receipt["groupAbsent"] = not group_members(leader["pgrp"])
            elif process is None:
                receipt["groupAbsent"] = True
        finally:
            custody.write_text(json.dumps(receipt, indent=2))


def require_native_absence(output, owner, stop=False):
    stages = owner.get("stages")
    if not isinstance(stages, list):
        raise RuntimeError("Native stage custody is unavailable; preserve containers")
    for stage in stages:
        path = output / (stage + "-custody.json")
        if not path.is_file():
            raise RuntimeError("Native stage custody receipt is missing; preserve containers")
        receipt = json.loads(path.read_text())
        leader = receipt.get("leader")
        if leader:
            if stop:
                quiesce(leader, owner["run"])
            if group_members(leader["pgrp"]):
                raise RuntimeError("Native clients remain; preserve containers")
        elif receipt.get("groupAbsent") is not True or receipt.get("gateReleased") is not False:
            raise RuntimeError("Native leader custody is unverified; preserve containers")


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
            require_native_absence(output, record, stop=True)
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
    if heads["defaults"] != "1fec4ef5c8c24fd57806b952182ae6bdb2d6a7fa" or heads["contracts"] != "78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7":
        raise ValueError("Joined shared dependency pins must match the reviewed source packet")
    pins = verify_sources(roots, heads)
    verify_composed_lock()
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
               # Match ordinary source CI's local-project graph only inside SDK children.
               GITHUB_ACTIONS="false", UseLocalMalievDependencies="true", UseSharedCompilation="false",
               CustomAfterMicrosoftCommonProps=str(PACKET / "composed-lock.props"),
               DOTNET_PROCESSOR_COUNT="2", MSBUILDDISABLENODEREUSE="1", DOTNET_CLI_TELEMETRY_OPTOUT="1",
               DOTNET_GCHeapHardLimit="0x100000000")
    properties = ["-p:RecoveryAuthRoot=" + str(roots["auth"]), "-p:RecoveryIntranetRoot=" + str(roots["intranet"]),
                  "-p:RecoveryNotificationRoot=" + str(roots["notification"]),
                  "-p:MalievWorkspaceRoot=" + str(roots["defaults"].parent), "-p:UseLocalMalievDependencies=true",
                  "-p:UseSharedCompilation=false", "-m:1", "-nr:false"]
    cleanup = {"run": run, "started": started.isoformat(), "expires": (started + dt.timedelta(minutes=50)).isoformat(),
               "stages": []}
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
        baseline_env = dict(env, N62_RESOURCE_DIRECTORY=str(args.output.resolve() / "resources-baseline"))
        with historical_model(roots["notification"], args.output):
            run_native(["dotnet", "build", "JoinedRecovery.AcceptanceTests.csproj", "-c", "Release", "--no-restore",
                        "-warnaserror", *properties], env, args.output, "baseline-build", run, deadline)
            run_native(["dotnet", "test", "JoinedRecovery.AcceptanceTests.csproj", "-c", "Release", "--no-build", "--no-restore",
                        "--filter", "FullyQualifiedName~JoinedRecoveryTests", "--logger", "trx", "--results-directory",
                        str(args.output.resolve() / "trx-baseline"), *properties], baseline_env, args.output,
                       "baseline-test", run, deadline, expected_exit=1)
            verify_baseline(args.output)
        verify_sources(roots, heads, args.output / "restored-source-changes.json")
        restored_env = dict(env, N62_RESOURCE_DIRECTORY=str(args.output.resolve() / "resources-restored"))
        run_native(["dotnet", "build", "JoinedRecovery.AcceptanceTests.csproj", "-c", "Release", "--no-restore",
                    "-warnaserror", *properties], env, args.output, "restored-build", run, deadline)
        run_native(["dotnet", "test", "JoinedRecovery.AcceptanceTests.csproj", "-c", "Release", "--no-build", "--no-restore",
                    "--logger", "trx", "--results-directory", str(args.output.resolve() / "trx-restored"),
                    *properties], restored_env, args.output, "restored-test", run, deadline)
        verify_restored(args.output)
        verify_composed_lock()
        verify_sources(roots, heads, args.output / "final-source-changes.json")
    finally:
        try:
            cleanup["stages"] = json.loads((args.output / "run-owner.json").read_text())["stages"]
            require_native_absence(args.output, cleanup)
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
