"""Ordinary hosted causal proof; no local SDK execution or real provider calls."""
import base64
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

CLASS = "Legacy.Maliev.NotificationService.Tests.Controllers.LegacyEncodedAttachmentNameHttpTests"
LEGACY = "LegacyMultipart_NameMatchesOriginalAttachmentAndRetainsExactBytes"
MODERN = "ModernJson_FilenameRemainsLiteralWithoutLegacyMimeDecoding"
CHANNELS = {"Info", "Manufacturing", "NoReply", "Support"}
FORMATS = {"Q", "B", "plain", "star"}
OLD = "710229c6d7285460c0d61574a4fea66bd408b0a3"
CONTROLLER = "Legacy.Maliev.NotificationService.Api/Controllers/EmailsController.cs"
TEST = "Legacy.Maliev.NotificationService.Tests/Controllers/LegacyEncodedAttachmentNameHttpTests.cs"
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def read_trx(path):
    tree = ET.parse(path)
    definitions = tree.findall("./t:TestDefinitions/t:UnitTest", NS)
    results = tree.findall("./t:Results/t:UnitTestResult", NS)
    require(len(definitions) == len(results), "Definition/result cardinality mismatch")
    identities, execution_ids = {}, set()
    for definition in definitions:
        method = definition.find("t:TestMethod", NS)
        execution = definition.find("t:Execution", NS)
        identity = definition.get("id")
        require(identity and identity not in identities and method is not None and execution is not None, "Invalid definition identity")
        execution_id = execution.get("id")
        require(execution_id and execution_id not in execution_ids, "Duplicate execution identity")
        execution_ids.add(execution_id)
        identities[identity] = (method.get("className", "").split(",", 1)[0].strip(), method.get("name"), definition.get("name"), execution_id)
    rows, seen = [], set()
    for result in results:
        identity = result.get("testId")
        require(identity in identities and identity not in seen, "Invalid/duplicate result join")
        seen.add(identity)
        cls, method, name, execution_id = identities[identity]
        require(result.get("executionId") == execution_id and result.get("testName") == name, "Result identity mismatch")
        rows.append({"class": cls, "method": method, "name": name, "outcome": result.get("outcome"),
                     "message": result.findtext("t:Output/t:ErrorInfo/t:Message", default="", namespaces=NS).strip(),
                     "stack": result.findtext("t:Output/t:ErrorInfo/t:StackTrace", default="", namespaces=NS),
                     "errorInfo": result.find("t:Output/t:ErrorInfo", NS) is not None})
    summary = tree.find("./t:ResultSummary", NS)
    require(summary is not None, "Missing result summary")
    counters = summary.find("t:Counters", NS)
    require(counters is not None, "Missing counters")
    require({"total", "executed", "passed", "failed"}.issubset(counters.attrib), "Missing mandatory counters")
    expected = {"total": len(rows), "executed": len(rows), "passed": sum(r["outcome"] == "Passed" for r in rows),
                "failed": sum(r["outcome"] == "Failed" for r in rows)}
    for identity, value in counters.attrib.items():
        require(int(value) == expected.get(identity, 0), "Counter mismatch/skip/timeout")
    require(summary.get("outcome") == ("Failed" if expected["failed"] else "Completed"), "Summary outcome mismatch")
    return rows


def verify_focus(path, baseline):
    rows = read_trx(path)
    return verify_focus_rows(rows, baseline)


def verify_focus_rows(rows, baseline):
    require(len(rows) == 20, "Focused proof requires exactly twenty executions")
    seen = set()
    expected_message = "Attachment payload name mismatch; expected UTF8 base64=" + base64.b64encode("งาน drawing.txt".encode()).decode() + "; actual UTF8 base64=" + base64.b64encode(b"=?utf-8?Q?=E0=B8=87=E0=B8=B2=E0=B8=99_drawing.txt?=").decode() + "."
    for row in rows:
        require(row["class"] == CLASS, "Wrong focus class")
        if row["method"] == LEGACY:
            match = re.fullmatch(re.escape(CLASS + "." + LEGACY) + r'\(channel: "([A-Za-z]+)", format: "([A-Za-z]+)"\)', row["name"] or "")
            require(match is not None, "Invalid legacy arguments")
            channel, format_name = match.groups()
            require(channel in CHANNELS and format_name in FORMATS, "Unexpected legacy arguments")
        else:
            require(row["method"] == MODERN, "Unexpected focus method")
            match = re.fullmatch(re.escape(CLASS + "." + MODERN) + r'\(channel: "([A-Za-z]+)"\)', row["name"] or "")
            require(match is not None and match.group(1) in CHANNELS, "Invalid modern arguments")
            channel, format_name = match.group(1), "JSON"
        identity = (channel, format_name)
        require(identity not in seen, "Duplicate focused arguments")
        seen.add(identity)
        failed = baseline and format_name == "Q"
        require(row["outcome"] == ("Failed" if failed else "Passed"), "Unexpected focused outcome")
        if failed:
            require(row["message"] == expected_message and ".VerifyPayload(" in row["stack"]
                    and CLASS + "." + LEGACY in row["stack"], "Failure is not exact Q payload-name mismatch")
        else:
            require(not row["errorInfo"], "Passing control contains an error")
    require(seen == {(c, f) for c in CHANNELS for f in FORMATS | {"JSON"}}, "Incomplete focus roster")
    return rows


def verify_full(path, inventory):
    rows = read_trx(path)
    require(len(rows) == 347 and all(r["outcome"] == "Passed" and not r["errorInfo"] for r in rows), "Full suite must pass all 347")
    prior = Counter((r["class"], r["method"], r["name"]) for r in rows if r["class"] != CLASS)
    expected = Counter({(r["className"], r["method"], r["testName"]): r["executions"] for r in inventory["rows"]})
    require(sum(expected.values()) == 327 and prior == expected, "Full suite changed accepted 327 identities")
    verify_focus_rows([r for r in rows if r["class"] == CLASS], False)
    return rows


def build_ok(log):
    text = Path(log).read_text()
    require("Build succeeded." in text and "0 Warning(s)" in text and "0 Error(s)" in text
            and not re.search(r"\b(?:warning|error) [A-Z]+\d+", text, re.I), "Build not zero-warning/error")


def wait_group_absent(group_id, probe=None, now=time.monotonic, pause=time.sleep, observations=None):
    if probe is None:
        probe = os.killpg
    started = now()
    deadline = started + 15
    while True:
        observed = {"elapsedSeconds": now() - started, "signal": 0, "groupId": group_id}
        if observations is not None:
            observations.append(observed)
        try:
            probe(group_id, 0)
        except ProcessLookupError:
            observed["state"] = "absent"
            return True
        except BaseException as exception:
            observed["state"] = "error"
            observed["errorType"] = type(exception).__name__
            raise
        observed["state"] = "present"
        if now() >= deadline:
            return False
        pause(0.1)


def record_group_exit(record, receipt, commands, probe=None, now=time.monotonic, pause=time.sleep):
    record["groupObservationStartedUtc"] = datetime.now(timezone.utc).isoformat()
    record["groupObservations"] = []
    try:
        record["ownedTimeoutGroupAbsent"] = wait_group_absent(
            record["pid"], probe, now, pause, record["groupObservations"])
        require(record["ownedTimeoutGroupAbsent"], "Owned command group did not exit; do not start another worker")
    finally:
        record["groupObservationEndedUtc"] = datetime.now(timezone.utc).isoformat()
        record["endedUtc"] = datetime.now(timezone.utc).isoformat()
        receipt.write_text(json.dumps(commands, indent=2) + "\n")


def main():
    require(sys.platform == "linux" and os.environ.get("GITHUB_ACTIONS") == "true", "Hosted ordinary runner only; no local SDK custody")
    root = Path(sys.argv[1]).resolve()
    repo = Path.cwd()
    require(root == repo / "runner-results", "Unexpected evidence root")
    output = root / "attachment-name-causal"
    output.mkdir(exist_ok=False)
    controller, test = repo / CONTROLLER, repo / TEST
    corrected, test_bytes = controller.read_bytes(), test.read_bytes()
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], timeout=20).decode().strip()
    require(subprocess.check_output(["git", "show", head + ":" + CONTROLLER], timeout=20) == corrected
            and subprocess.check_output(["git", "show", head + ":" + TEST], timeout=20) == test_bytes, "Dirty source inputs")
    original = subprocess.check_output(["git", "show", OLD + ":" + CONTROLLER], timeout=20)
    require(corrected.replace(b"            // The original controller passed filenames through Attachment.Name, which decodes MIME encoded words.\n", b"")
            .replace(b"            using var legacyName = new System.Net.Mail.Attachment(Stream.Null, file.FileName);\n", b"")
            .replace(b"legacyName.Name!", b"file.FileName") == original, "Correction-disabled source is not exact accepted controller")
    inventory = json.loads((repo / "docs/attachment-name-prior327-inventory.json").read_text())
    original_full = [p for p in root.rglob("*.trx") if output not in p.parents]
    require(len(original_full) == 1, "Expected one original full-suite TRX")
    verify_full(original_full[0], inventory)
    commands = []
    dotnet, timeout = shutil.which("dotnet"), shutil.which("timeout")
    require(dotnet and timeout, "Missing ordinary runner tools")

    def run(name, arguments, seconds):
        available = next(line.split()[1] for line in Path("/proc/meminfo").read_text().splitlines() if line.startswith("MemAvailable:"))
        require(int(available) >= 4194304, "Fixed 4GiB memory admission blocked")
        command = [timeout, "--signal=TERM", "--kill-after=15s", str(seconds) + "s", dotnet, *arguments]
        record = {"name": name, "command": command, "startedUtc": datetime.now(timezone.utc).isoformat()}
        commands.append(record)
        with (output / (name + ".log")).open("wb") as log:
            # GNU timeout owns the bounded process group; no detached SDK workers or imported job roles.
            with subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT) as process:
                record["pid"] = process.pid
                stat = Path(f"/proc/{process.pid}/stat").read_text()
                record["kernelStartTicks"] = stat[stat.rfind(")") + 2:].split()[19]
                record["executable"] = os.readlink(f"/proc/{process.pid}/exe")
                record["returnCode"] = process.wait()
                record["exitVerified"] = process.poll() is not None
                record_group_exit(record, output / "command-custody.json", commands)
        return record["returnCode"]

    build = ["build", "Legacy.Maliev.NotificationService.slnx", "--configuration", "Release", "--no-restore", "--disable-build-servers",
             "-p:UseLocalMalievDependencies=true", "-p:GITHUB_ACTIONS=false", "-p:UseSharedCompilation=false", "-nodeReuse:false", "-warnaserror"]
    tests = ["test", "Legacy.Maliev.NotificationService.Tests/Legacy.Maliev.NotificationService.Tests.csproj", "--configuration", "Release",
             "--no-build", "--no-restore", "--disable-build-servers", "-p:UseLocalMalievDependencies=true", "-p:GITHUB_ACTIONS=false", "-p:VSTestCollect="]
    error = None
    try:
        require(run("candidate-focus", tests + ["--filter", "FullyQualifiedName~LegacyEncodedAttachmentNameHttpTests", "--logger", "trx;LogFileName=candidate.trx", "--results-directory", str(output)], 120) == 0, "Candidate focus failed")
        verify_focus(output / "candidate.trx", False)
        controller.write_bytes(original)
        require(run("baseline-build", build, 180) == 0, "Baseline build failed")
        build_ok(output / "baseline-build.log")
        require(test.read_bytes() == test_bytes and controller.read_bytes() == original, "Baseline source changed")
        require(run("baseline-focus", tests + ["--filter", "FullyQualifiedName~LegacyEncodedAttachmentNameHttpTests", "--logger", "trx;LogFileName=baseline.trx", "--results-directory", str(output)], 120) == 1, "Baseline exit is not exactly one")
        verify_focus(output / "baseline.trx", True)
    except BaseException as exception:
        error = exception
    finally:
        controller.write_bytes(corrected)
        require(controller.read_bytes() == corrected and test.read_bytes() == test_bytes, "Corrected source restore failed")
        (output / "command-custody.json").write_text(json.dumps(commands, indent=2) + "\n")
    # A failed baseline still restores source; it cannot be treated as a green causal proof.
    if error:
        raise error
    require(run("restored-build", build, 180) == 0, "Restored build failed")
    build_ok(output / "restored-build.log")
    require(run("restored-full", tests + ["--logger", "trx;LogFileName=restored-full.trx", "--results-directory", str(output)], 300) == 0, "Restored full suite failed")
    restored = verify_full(output / "restored-full.trx", inventory)
    require(controller.read_bytes() == corrected and test.read_bytes() == test_bytes, "Final source identity changed")
    receipt = {"head": head, "baselineControllerSource": OLD, "correctedControllerSha256": digest(corrected),
               "baselineControllerSha256": digest(original), "immutableTestSha256": digest(test_bytes),
               "baselineFourQFailedSixteenControlsPassed": True, "candidateTwentyPassed": True, "restoredFullPassed": len(restored),
               "prior327IdentitiesRetained": True, "originalFullTrxSha256": digest(original_full[0].read_bytes()),
               "sourceRestored": True, "commands": commands,
               "rawFiles": {p.name: digest(p.read_bytes()) for p in output.iterdir() if p.suffix in {".trx", ".log"}}}
    (output / "command-custody.json").write_text(json.dumps(commands, indent=2) + "\n")
    (output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")


if __name__ == "__main__":
    main()
