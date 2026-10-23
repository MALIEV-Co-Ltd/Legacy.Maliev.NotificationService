import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET
import causal_baseline as causal


class CausalBaselineTests(unittest.TestCase):
    def test_exact_historical_mutation_restores_after_failure(self):
        original = (Path(__file__).resolve().parents[2] / causal.MODEL).read_bytes()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            model = root / causal.MODEL
            model.parent.mkdir(parents=True)
            model.write_bytes(original)
            with self.assertRaisesRegex(RuntimeError, "controlled native failure"):
                with causal.historical_model(root, root):
                    self.assertEqual(causal.BASELINE_SHA256, hashlib.sha256(model.read_bytes()).hexdigest())
                    raise RuntimeError("controlled native failure")
            self.assertEqual(original, model.read_bytes())
            self.assertTrue(json.loads((root / "causal-source.json").read_text())["restored"])
            model.write_bytes(original + b"// unexpected source\n")
            with self.assertRaisesRegex(ValueError, "reviewed model"):
                with causal.historical_model(root, root):
                    self.fail("Unexpected source admitted")
            self.assertEqual(original + b"// unexpected source\n", model.read_bytes())

    def data(self):
        rows, receipts = [], []
        for purpose in ("password-reset", "email-confirmation"):
            for quoted in (False, True):
                for status in (201, 400):
                    rows.append({"testId": str(len(rows)), "testName":
                        'JoinedRecoveryTests.NormalJoin_PreservesMailbox_AndConsumesChallengeExactlyOnce'
                        f'(purpose: "{purpose}", quoted: {quoted}, providerStatus: {status})',
                        "outcome": "Failed" if quoted else "Passed", "message":
                        "Assert.Single() Failure: The collection was empty", "stack":
                        "at JoinedRecoveryTests.NormalJoin_PreservesMailbox_AndConsumesChallengeExactlyOnce"})
                    receipts.append({"fixtureId": str(len(receipts)), "phase": "disposed", "cleanupFailures": [],
                        "dependentCleanupDeferred": False, "allRegisteredPoolsCleared": True,
                        "hostDisposal": [{"disposalCompletedSuccessfully": True}] * 3,
                        "authRecoveryRequests": [200], "notificationDeliveryStatuses": [400 if quoted else 200 if status == 201 else 502],
                        "providerDeliveryCount": 0 if quoted else 1})
        return rows, receipts

    def write(self, root, rows, receipts):
        tag = lambda name: "{" + causal.NS["t"] + "}" + name
        run = ET.Element(tag("TestRun"))
        results = ET.SubElement(run, tag("Results"))
        for row in rows:
            result = ET.SubElement(results, tag("UnitTestResult"), {key: row[key] for key in ("testId", "testName", "outcome")})
            info = ET.SubElement(ET.SubElement(result, tag("Output")), tag("ErrorInfo"))
            ET.SubElement(info, tag("Message")).text = row["message"]
            ET.SubElement(info, tag("StackTrace")).text = row["stack"]
        (root / "trx-baseline").mkdir()
        ET.ElementTree(run).write(root / "trx-baseline/results.trx")
        (root / "resources-baseline").mkdir()
        for index, receipt in enumerate(receipts):
            (root / f"resources-baseline/{index}.json").write_text(json.dumps(receipt))

    def test_exact_cases_and_route_rejections_are_required(self):
        for defect in (None, "missing", "duplicate", "outcome", "unrelated failure", "wrong route", "cleanup", "missing fixture"):
            with self.subTest(defect=defect), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                rows, receipts = self.data()
                if defect == "missing": rows.pop()
                if defect == "duplicate": rows[0] = copy.deepcopy(rows[1])
                if defect == "outcome": rows[0]["outcome"] = "Failed"
                if defect == "unrelated failure": rows[2]["message"] = "Unrelated failure"
                if defect == "wrong route": receipts[2]["notificationDeliveryStatuses"] = [401]
                if defect == "cleanup": receipts[0]["cleanupFailures"] = ["retained host"]
                if defect == "missing fixture": receipts.pop()
                self.write(root, rows, receipts)
                if defect:
                    with self.assertRaises(ValueError): causal.verify_baseline(root)
                    self.assertFalse((root / "causal-results.json").exists())
                else:
                    causal.verify_baseline(root)
                    self.assertEqual("PASS", json.loads((root / "causal-results.json").read_text())["result"])

    def test_restored_run_requires_all_twelve_exact_cases_without_skips(self):
        for defect in (None, "missing", "duplicate", "skip", "failure"):
            with self.subTest(defect=defect), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                rows, receipts = self.data()
                names = ["HostPoolCleanupRegressionTests.CleanupRetainsEffectiveAuthPoolsAlongsideFixturePools",
                         "HostDisposalFailureRegressionTests.DependentsReleaseOnlyAfterEverySuccessfulHostDisposal",
                         "HostDisposalFailureRegressionTests.FailedOrTimedOutDisposalPreservesPoolsAndContainers(timeout: False)",
                         "HostDisposalFailureRegressionTests.FailedOrTimedOutDisposalPreservesPoolsAndContainers(timeout: True)"]
                rows.extend({"testId": str(index + 8), "testName": name, "message": "", "stack": ""}
                            for index, name in enumerate(names))
                for row in rows: row["outcome"] = "Passed"
                if defect == "missing": rows.pop()
                if defect == "duplicate": rows[0] = copy.deepcopy(rows[1])
                if defect == "skip": rows[0]["outcome"] = "NotExecuted"
                if defect == "failure": rows[0]["outcome"] = "Failed"
                self.write(root, rows, receipts)
                (root / "trx-baseline").rename(root / "trx-restored")
                if defect:
                    with self.assertRaises(ValueError): causal.verify_restored(root)
                    self.assertFalse((root / "restored-results.json").exists())
                else:
                    causal.verify_restored(root)
                    self.assertEqual("PASS", json.loads((root / "restored-results.json").read_text())["result"])


if __name__ == "__main__":
    unittest.main()
