"""Exact disposable model baseline and strict genuine-join result qualification."""
from collections import Counter
from contextlib import contextmanager
import hashlib
import json
import xml.etree.ElementTree as ET

MODEL = "Legacy.Maliev.NotificationService.Api/Models/SendEmailNotificationModels.cs"
CURRENT_SHA256 = "5e9e89bd80d3a309dee01b4ec905ed73168bab15d11f0236b236bb21c918aaf1"
BASELINE_SHA256 = "fe3f1c40c16d70f938382d8f12a4119a00169af00fabfa975194aaa64bf0d895"
BASELINE_COMMIT = "e954dd7f9e0135d7f4119d378ad1079b61751cee"
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


@contextmanager
def historical_model(root, output):
    path = root / MODEL
    original = path.read_bytes()
    if hashlib.sha256(original).hexdigest() != CURRENT_SHA256:
        raise ValueError("Causal source differs from the reviewed model")
    baseline = original.replace(b"LegacyMailbox", b"EmailAddress")
    if original.count(b"LegacyMailbox") != 2 or hashlib.sha256(baseline).hexdigest() != BASELINE_SHA256:
        raise ValueError("Causal mutation differs from the exact historical model")
    receipt = {"path": MODEL, "beforeSha256": CURRENT_SHA256, "baselineSha256": BASELINE_SHA256,
               "baselineSourceCommit": BASELINE_COMMIT, "restored": False}
    record = output / "causal-source.json"
    record.write_text(json.dumps(receipt, indent=2))
    try:
        path.write_bytes(baseline)
        yield
    finally:
        path.write_bytes(original)
        receipt["restored"] = path.read_bytes() == original
        record.write_text(json.dumps(receipt, indent=2))
        if not receipt["restored"]:
            raise RuntimeError("Causal source restoration failed")


def joined_cases():
    return {
        'JoinedRecoveryTests.NormalJoin_PreservesMailbox_AndConsumesChallengeExactlyOnce'
        f'(purpose: "{purpose}", quoted: {quoted}, providerStatus: {status})': quoted == "True"
        for purpose in ("password-reset", "email-confirmation")
        for quoted in ("False", "True") for status in (201, 400)
    }


def verify_baseline(output):
    files = list((output / "trx-baseline").glob("*.trx"))
    if len(files) != 1:
        raise ValueError("Expected one causal TRX")
    rows = ET.parse(files[0]).findall("t:Results/t:UnitTestResult", NS)
    expected = joined_cases()
    if len(rows) != 8 or Counter(row.get("testName") for row in rows) != Counter(expected.keys()):
        raise ValueError("Causal case identities are missing, duplicated or unexpected")
    for row in rows:
        rejected = expected[row.get("testName")]
        if row.get("outcome") != ("Failed" if rejected else "Passed"):
            raise ValueError("Causal case outcome differs from quoted model rejection")
        if rejected:
            message = row.findtext("t:Output/t:ErrorInfo/t:Message", namespaces=NS) or ""
            stack = row.findtext("t:Output/t:ErrorInfo/t:StackTrace", namespaces=NS) or ""
            if "Assert.Single() Failure: The collection was empty" not in message or "JoinedRecoveryTests.NormalJoin_" not in stack:
                raise ValueError("Causal failure is not the expected missing provider delivery")
    receipts = [json.loads(path.read_text()) for path in (output / "resources-baseline").glob("*.json")]
    if len(receipts) != 8 or len({row["fixtureId"] for row in receipts}) != 8:
        raise ValueError("Causal fixture custody is incomplete")
    delivery_status = Counter()
    for row in receipts:
        if (row["phase"] != "disposed" or row["cleanupFailures"] or row["dependentCleanupDeferred"]
                or not row["allRegisteredPoolsCleared"] or len(row["hostDisposal"]) != 3
                or not all(host["disposalCompletedSuccessfully"] for host in row["hostDisposal"])):
            raise ValueError("Causal fixture cleanup is incomplete")
        requests = row["authRecoveryRequests"]
        deliveries = row["notificationDeliveryStatuses"]
        if len(requests) != 1 or requests[0] != 200 or len(deliveries) != 1:
            raise ValueError("Causal real Auth/Notification route evidence is incomplete")
        status = deliveries[0]
        if row["providerDeliveryCount"] != (0 if status == 400 else 1):
            raise ValueError("Causal provider capture contradicts model admission")
        delivery_status[status] += 1
    if delivery_status != Counter({400: 4, 200: 2, 502: 2}):
        raise ValueError("Causal Notification model rejection/control statuses differ")
    (output / "causal-results.json").write_text(json.dumps({
        "result": "PASS", "quotedRejected": 4, "ordinaryPassed": 4,
        "notificationStatuses": dict(delivery_status),
        "cases": [{"id": row.get("testId"), "name": row.get("testName"), "outcome": row.get("outcome")} for row in rows],
    }, indent=2))


def verify_restored(output):
    expected = set(joined_cases()) | {
        "HostPoolCleanupRegressionTests.CleanupRetainsEffectiveAuthPoolsAlongsideFixturePools",
        "HostDisposalFailureRegressionTests.DependentsReleaseOnlyAfterEverySuccessfulHostDisposal",
        "HostDisposalFailureRegressionTests.FailedOrTimedOutDisposalPreservesPoolsAndContainers(timeout: False)",
        "HostDisposalFailureRegressionTests.FailedOrTimedOutDisposalPreservesPoolsAndContainers(timeout: True)",
    }
    files = list((output / "trx-restored").glob("*.trx"))
    if len(files) != 1:
        raise ValueError("Expected one restored TRX")
    rows = ET.parse(files[0]).findall("t:Results/t:UnitTestResult", NS)
    if (len(rows) != 12 or Counter(row.get("testName") for row in rows) != Counter(expected)
            or any(row.get("outcome") != "Passed" for row in rows)):
        raise ValueError("Restored run must pass all twelve exact cases without skips")
    (output / "restored-results.json").write_text(json.dumps({"result": "PASS", "cases": [
        {"id": row.get("testId"), "name": row.get("testName"), "outcome": row.get("outcome")} for row in rows
    ]}, indent=2))
