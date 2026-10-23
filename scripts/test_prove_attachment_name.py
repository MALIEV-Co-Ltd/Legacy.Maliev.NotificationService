"""Synthetic negative receipts; these do not run a renderer, SDK, HTTP host or provider."""
import base64
from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET
from prove_attachment_name import CHANNELS, CLASS, FORMATS, LEGACY, MODERN, build_ok, verify_focus, verify_full, wait_group_absent, record_group_exit

N = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


class AttachmentNameProofTests(unittest.TestCase):
    def test_group_exit_wait_is_bounded_and_never_terminates(self):
        clock = [0.0]
        calls = []
        def pause(seconds):
            clock[0] += seconds
        def delayed(group, signal):
            calls.append((group, signal))
            if clock[0] >= 0.2:
                raise ProcessLookupError
        self.assertTrue(wait_group_absent(42, delayed, lambda: clock[0], pause))
        self.assertTrue(all(call == (42, 0) for call in calls))
        clock[0] = 0.0
        self.assertFalse(wait_group_absent(42, lambda group, signal: None, lambda: clock[0], pause))
        self.assertLess(clock[0], 15.2)

    def test_group_permission_error_is_fail_closed_with_receipt(self):
        def denied(group, signal):
            raise PermissionError("synthetic")
        record = {"pid": 42, "kernelStartTicks": "123", "executable": "/usr/bin/timeout"}
        next_worker = []
        with tempfile.TemporaryDirectory() as directory:
            receipt = Path(directory) / "receipt.json"
            with self.assertRaises(PermissionError):
                record_group_exit(record, receipt, [record], denied)
                next_worker.append(True)
            self.assertEqual([], next_worker)
            self.assertEqual("PermissionError", record["groupObservations"][0]["errorType"])
            self.assertIn('"groupObservationEndedUtc"', receipt.read_text())
            self.assertNotIn('"ownedTimeoutGroupAbsent": true', receipt.read_text())

    def test_permanent_group_failure_retains_timed_receipt(self):
        clock = [0.0]
        def pause(seconds):
            clock[0] += seconds
        record = {"pid": 42}
        with tempfile.TemporaryDirectory() as directory:
            receipt = Path(directory) / "receipt.json"
            with self.assertRaises(ValueError):
                record_group_exit(record, receipt, [record], lambda group, signal: None, lambda: clock[0], pause)
            self.assertFalse(record["ownedTimeoutGroupAbsent"])
            self.assertTrue(receipt.exists())
            self.assertLess(clock[0], 15.2)

    def fixture(self, baseline=False):
        root = ET.Element(N + "TestRun")
        definitions = ET.SubElement(root, N + "TestDefinitions")
        results = ET.SubElement(root, N + "Results")
        expected = base64.b64encode("งาน drawing.txt".encode()).decode()
        actual = base64.b64encode(b"=?utf-8?Q?=E0=B8=87=E0=B8=B2=E0=B8=99_drawing.txt?=").decode()
        for index, (channel, format_name) in enumerate((c, f) for c in sorted(CHANNELS) for f in sorted(FORMATS | {"JSON"})):
            modern = format_name == "JSON"
            method = MODERN if modern else LEGACY
            name = CLASS + "." + method + (f'(channel: "{channel}")' if modern else f'(channel: "{channel}", format: "{format_name}")')
            definition = ET.SubElement(definitions, N + "UnitTest", id=str(index), name=name)
            ET.SubElement(definition, N + "TestMethod", className=CLASS, name=method)
            ET.SubElement(definition, N + "Execution", id="execution" + str(index))
            failed = baseline and format_name == "Q"
            result = ET.SubElement(results, N + "UnitTestResult", testId=str(index), executionId="execution" + str(index), testName=name,
                                   outcome="Failed" if failed else "Passed")
            if failed:
                error = ET.SubElement(ET.SubElement(result, N + "Output"), N + "ErrorInfo")
                ET.SubElement(error, N + "Message").text = f"Attachment payload name mismatch; expected UTF8 base64={expected}; actual UTF8 base64={actual}."
                ET.SubElement(error, N + "StackTrace").text = "at " + CLASS + ".VerifyPayload(JsonElement payload)\nat " + CLASS + "." + LEGACY + "(String channel, String format)"
        summary = ET.SubElement(root, N + "ResultSummary", outcome="Failed" if baseline else "Completed")
        ET.SubElement(summary, N + "Counters", total="20", executed="20", passed="16" if baseline else "20", failed="4" if baseline else "0", notExecuted="0", timeout="0")
        return root

    def check(self, root, baseline=True):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "focus.trx"
            ET.ElementTree(root).write(path, encoding="utf-8")
            return verify_focus(path, baseline)

    def test_exact_four_red_sixteen_controls_and_twenty_green(self):
        self.assertEqual(20, len(self.check(self.fixture(True))))
        self.assertEqual(20, len(self.check(self.fixture(False), False)))

    def test_wrong_failure_type_payload_or_boundary_is_rejected(self):
        for value in ("Assert.Single() Failure", "HTTP status 401", "timed out", "Attachment payload name mismatch; expected UTF8 base64=wrong; actual UTF8 base64=wrong."):
            root = self.fixture(True)
            root.find(".//" + N + "Message").text = value
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.check(root)
        root = self.fixture(True)
        root.find(".//" + N + "StackTrace").text = "at OriginalNameOracle"
        with self.assertRaises(ValueError):
            self.check(root)

    def test_ids_classes_methods_arguments_counters_and_outcomes_fail_closed(self):
        mutations = [
            lambda r: r.find(".//" + N + "TestMethod").set("className", "Wrong.Class"),
            lambda r: r.find(".//" + N + "TestMethod").set("name", "WrongMethod"),
            lambda r: r.find(".//" + N + "UnitTestResult").set("testId", "unknown"),
            lambda r: r.find(".//" + N + "UnitTestResult").set("executionId", "wrong"),
            lambda r: r.find(".//" + N + "UnitTestResult").set("testName", "wrong"),
            lambda r: r.find(".//" + N + "UnitTestResult").set("outcome", "Failed"),
            lambda r: r.find(".//" + N + "Counters").set("passed", "20"),
            lambda r: r.find(".//" + N + "Counters").set("timeout", "1"),
            lambda r: r.find(N + "ResultSummary").set("outcome", "Completed"),
            lambda r: r.find(N + "Results").remove(r.find(".//" + N + "UnitTestResult")),
        ]
        for index, mutation in enumerate(mutations):
            root = self.fixture(True)
            mutation(root)
            with self.subTest(mutation=index), self.assertRaises(ValueError):
                self.check(root)

    def test_duplicate_results_and_duplicate_arguments_fail_closed(self):
        for kind in ("result", "arguments"):
            root = self.fixture(True)
            results, definitions = root.find(N + "Results"), root.find(N + "TestDefinitions")
            if kind == "result":
                results.remove(results[-1])
                results.append(deepcopy(results[0]))
            else:
                definitions[1].set("name", definitions[0].get("name"))
                results[1].set("testName", results[0].get("testName"))
            with self.assertRaises(ValueError):
                self.check(root)

    def test_warning_error_or_absent_build_result_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "build.log"
            for text in ("no build", "Build succeeded.\n1 Warning(s)\n0 Error(s)", "Build succeeded.\n0 Warning(s)\n1 Error(s)",
                         "Build succeeded.\n0 Warning(s)\n0 Error(s)\nwarning CS1591"):
                path.write_text(text)
                with self.subTest(text=text), self.assertRaises(ValueError):
                    build_ok(path)
            path.write_text("Build succeeded.\n0 Warning(s)\n0 Error(s)")
            build_ok(path)

    def test_incomplete_full_suite_cannot_replace_prior327(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "full.trx"
            ET.ElementTree(self.fixture(False)).write(path)
            with self.assertRaises(ValueError):
                verify_full(path, {"rows": []})

    def full_fixture(self):
        root = self.fixture(False)
        inventory = json.loads((Path(__file__).resolve().parents[1] / "docs/attachment-name-prior327-inventory.json").read_text())
        definitions, results = root.find(N + "TestDefinitions"), root.find(N + "Results")
        index = 20

        def append(cls, method, name):
            nonlocal index
            identity, execution = "full" + str(index), "full-execution" + str(index)
            definition = ET.SubElement(definitions, N + "UnitTest", id=identity, name=name)
            ET.SubElement(definition, N + "TestMethod", className=cls, name=method)
            ET.SubElement(definition, N + "Execution", id=execution)
            ET.SubElement(results, N + "UnitTestResult", testId=identity, executionId=execution, testName=name, outcome="Passed")
            index += 1

        for row in inventory["rows"]:
            for _ in range(row["executions"]):
                append(row["className"], row["method"], row["testName"])
        observer = "Legacy.Maliev.NotificationService.Tests.Controllers.NotificationHostTransportHttpTests"
        for method in (
            "RegisteredHealth_IdentifiesOnlyTheServingHostWithoutSuccessfulProbeLogs",
            "SyntheticObservation_RequiresNativeLoopbackAndNonceAndSharesTheHealthIdentity",
            "OrdinaryProviderFailure_RecordsOneHandledFailureWithoutHealthOrSyntheticIdentity",
        ):
            append(observer, method, observer + "." + method)
        counters = root.find(".//" + N + "Counters")
        for name in ("total", "executed", "passed"):
            counters.set(name, "350")
        return root, inventory

    def check_full(self, root, inventory):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "full.trx"
            ET.ElementTree(root).write(path, encoding="utf-8")
            return verify_full(path, inventory)

    def test_full350_retains_all_frozen327_and_twenty_attachment_identities(self):
        root, inventory = self.full_fixture()
        rows = self.check_full(root, inventory)
        self.assertEqual(350, len(rows))
        self.assertEqual(22, sum(row["class"] == "Legacy.Maliev.NotificationService.Tests.Controllers.NotificationHostTransportHttpTests"
                                 and not row["name"].endswith((
                                     ".RegisteredHealth_IdentifiesOnlyTheServingHostWithoutSuccessfulProbeLogs",
                                     ".SyntheticObservation_RequiresNativeLoopbackAndNonceAndSharesTheHealthIdentity",
                                     ".OrdinaryProviderFailure_RecordsOneHandledFailureWithoutHealthOrSyntheticIdentity",
                                 )) for row in rows))

    def test_full_rejects_missing_duplicate_or_renamed_observer(self):
        for mutation in ("missing", "duplicate", "renamed", "argument"):
            root, inventory = self.full_fixture()
            definitions, results = root.find(N + "TestDefinitions"), root.find(N + "Results")
            if mutation == "missing":
                definitions.remove(definitions[-1])
                results.remove(results[-1])
                for key in ("total", "executed", "passed"):
                    root.find(".//" + N + "Counters").set(key, "349")
            elif mutation == "duplicate":
                definitions[-1].set("name", definitions[-2].get("name"))
                definitions[-1].find(N + "TestMethod").set("name", definitions[-2].find(N + "TestMethod").get("name"))
                results[-1].set("testName", definitions[-2].get("name"))
            else:
                method = definitions[-1].find(N + "TestMethod")
                if mutation == "renamed":
                    method.set("name", method.get("name") + "Renamed")
                name = definitions[-1].get("name") + ("Renamed" if mutation == "renamed" else "(unexpected: true)")
                definitions[-1].set("name", name)
                results[-1].set("testName", name)
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                self.check_full(root, inventory)

    def test_full_rejects_replaced_old_host_identity_and_extra_wrong_identity(self):
        for replacement in ("old-host", "observer"):
            root, inventory = self.full_fixture()
            definitions, results = root.find(N + "TestDefinitions"), root.find(N + "Results")
            index = -1 if replacement == "observer" else next(
                index for index, definition in enumerate(definitions[:-3])
                if definition.find(N + "TestMethod").get("className").endswith(".NotificationHostTransportHttpTests"))
            method = definitions[index].find(N + "TestMethod")
            method.set("name", "UnreviewedReplacement")
            name = method.get("className") + ".UnreviewedReplacement"
            definitions[index].set("name", name)
            results[index].set("testName", name)
            with self.subTest(replacement=replacement), self.assertRaises(ValueError):
                self.check_full(root, inventory)

    def test_full_rejects_errors_skips_and_counter_spoof(self):
        for mutation in ("failed", "skipped", "error-info", "run-error", "counter", "nested-summary", "nested-counter", "nested-definition", "nested-result"):
            root, inventory = self.full_fixture()
            result = root.find(".//" + N + "UnitTestResult")
            counters = root.find(".//" + N + "Counters")
            if mutation == "failed":
                result.set("outcome", "Failed")
                counters.set("passed", "349")
                counters.set("failed", "1")
                root.find(N + "ResultSummary").set("outcome", "Failed")
            elif mutation == "skipped":
                result.set("outcome", "NotExecuted")
                counters.set("passed", "349")
            elif mutation == "error-info":
                ET.SubElement(ET.SubElement(result, N + "Output"), N + "ErrorInfo")
            elif mutation == "run-error":
                ET.SubElement(ET.SubElement(root.find(N + "ResultSummary"), N + "RunInfos"), N + "RunInfo", outcome="Error")
            elif mutation == "counter":
                counters.set("passed", "351")
            elif mutation == "nested-summary":
                ET.SubElement(ET.SubElement(root, N + "Extension"), N + "ResultSummary", outcome="Failed")
            elif mutation == "nested-counter":
                ET.SubElement(ET.SubElement(root, N + "Extension"), N + "Counters", failed="1")
            else:
                name = "UnitTest" if mutation == "nested-definition" else "UnitTestResult"
                ET.SubElement(ET.SubElement(root, N + "Extension"), N + name)
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                self.check_full(root, inventory)



if __name__ == "__main__":
    unittest.main()
