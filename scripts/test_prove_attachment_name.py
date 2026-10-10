"""Synthetic negative receipts; these do not run a renderer, SDK, HTTP host or provider."""
import base64
from copy import deepcopy
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET
from prove_attachment_name import CHANNELS, CLASS, FORMATS, LEGACY, MODERN, build_ok, verify_focus, verify_full

N = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


class AttachmentNameProofTests(unittest.TestCase):
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


if __name__ == "__main__":
    unittest.main()
