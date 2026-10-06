"""Pure orchestration tests: no Docker, containers, hosts or target assemblies."""
import json
import importlib.util
from pathlib import Path
import subprocess
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location("preload_postgres_image", Path(__file__).with_name("preload-postgres-image.py"))
subject = importlib.util.module_from_spec(spec)
spec.loader.exec_module(subject)

DIGEST = "postgres@sha256:" + "a" * 64
METADATA = json.dumps({"digests": [DIGEST], "environment": ["PG_MAJOR=18"], "os": "linux"})


class PreloadTests(unittest.TestCase):
    def exercise(self, replies):
        calls, pauses = [], []

        def runner(arguments, timeout):
            calls.append((arguments, timeout))
            reply = replies.pop(0)
            if isinstance(reply, Exception):
                raise reply
            return subprocess.CompletedProcess(arguments, *reply)

        return lambda: subject.preload(runner, pauses.append), calls, pauses

    def test_ready_retains_exact_digest_and_existing_image(self):
        run, calls, pauses = self.exercise([(0, "pulled"), (0, METADATA)])
        result = run()
        self.assertEqual((result["digest"], result["image"], result["attempts"]), (DIGEST, "postgres:18-alpine", 1))
        self.assertEqual(calls[0], (["pull", "--quiet", "postgres:18-alpine"], 90))
        self.assertEqual(calls[1][1], 15)
        self.assertEqual(pauses, [])

    def test_actual_reset_then_ready_uses_only_bounded_pull_retry(self):
        run, calls, pauses = self.exercise([(1, "read: connection reset by peer"), (0, "pulled"), (0, METADATA)])
        self.assertEqual(run()["attempts"], 2)
        self.assertEqual(pauses, [2])
        self.assertEqual(len(calls), 3)

    def test_two_transport_failures_then_ready_use_fixed_backoff(self):
        run, _, pauses = self.exercise([(1, "tls handshake timeout"), (1, "unexpected EOF"), (0, "pulled"), (0, METADATA)])
        self.assertEqual(run()["attempts"], 3)
        self.assertEqual(pauses, [2, 5])

    def test_exhausted_transport_failure_fails_closed_at_three(self):
        run, calls, pauses = self.exercise([(1, "connection reset by peer")] * 3)
        with self.assertRaises(ValueError):
            run()
        self.assertEqual((len(calls), pauses), (3, [2, 5]))

    def test_denied_credentials_are_not_retried(self):
        run, calls, pauses = self.exercise([(1, "unauthorized: authentication required")])
        with self.assertRaises(ValueError):
            run()
        self.assertEqual((len(calls), pauses), (1, []))

    def test_rate_capacity_or_manifest_failure_overrides_transport_text(self):
        for message in ("429 connection reset by peer", "no space: unexpected EOF", "manifest unknown: i/o timeout"):
            with self.subTest(message=message):
                run, calls, pauses = self.exercise([(1, message)])
                with self.assertRaises(ValueError):
                    run()
                self.assertEqual((len(calls), pauses), (1, []))

    def test_unknown_failure_is_not_retried(self):
        run, calls, _ = self.exercise([(1, "an unknown daemon failure")])
        with self.assertRaises(ValueError):
            run()
        self.assertEqual(len(calls), 1)

    def test_process_timeout_is_not_retried(self):
        run, calls, pauses = self.exercise([subprocess.TimeoutExpired("docker", 90)])
        with self.assertRaises(subprocess.TimeoutExpired):
            run()
        self.assertEqual((len(calls), pauses), (1, []))

    def test_failed_inspection_does_not_retry_successful_pull(self):
        run, calls, pauses = self.exercise([(0, "pulled"), (1, "inspection failed")])
        with self.assertRaises(ValueError):
            run()
        self.assertEqual((len(calls), pauses), (2, []))

    def test_wrong_major_wrong_repository_or_missing_digest_reject(self):
        for metadata in ({"digests": [DIGEST], "environment": ["PG_MAJOR=17"], "os": "linux"},
                         {"digests": ["other@sha256:" + "a" * 64], "environment": ["PG_MAJOR=18"], "os": "linux"},
                         {"digests": [], "environment": ["PG_MAJOR=18"], "os": "linux"}):
            with self.subTest(metadata=metadata):
                run, _, _ = self.exercise([(0, "pulled"), (0, json.dumps(metadata))])
                with self.assertRaises(ValueError):
                    run()

    def test_unbounded_or_malformed_metadata_reject(self):
        for metadata in ("x" * 8193, "not JSON", "{}"):
            with self.subTest(metadata=metadata):
                with self.assertRaises(ValueError):
                    subject.verify_image(metadata)

    def test_oversized_diagnostic_is_not_a_retry_authority(self):
        self.assertFalse(subject.transient_transport("connection reset by peer" + "x" * 8192))


class ProcessCleanupTests(unittest.TestCase):
    def exercise(self, identities, responses, polls):
        process = Mock(pid=123, returncode=0)
        process.communicate.side_effect = responses
        process.poll.side_effect = polls
        subject.PROCESS_LEDGER.clear()
        with patch.object(subject.subprocess, "Popen", return_value=process), patch.object(subject, "process_identity", side_effect=identities):
            with self.assertRaises(subprocess.TimeoutExpired):
                subject.execute(["pull", "--quiet", subject.IMAGE], 90)
        return process, subject.PROCESS_LEDGER[0]

    def test_timeout_terminates_only_matching_child_and_verifies_exit(self):
        identity = {"pid": 123, "kernelStartTicks": 45, "executable": "/usr/bin/docker"}
        process, record = self.exercise([identity, identity], [subprocess.TimeoutExpired("docker", 90), ("", None)], [None, 0, 0])
        process.terminate.assert_called_once()
        process.kill.assert_not_called()
        self.assertEqual(record["state"], "verified exited")
        process.stdout.close.assert_called_once()

    def test_changed_identity_is_preserved_and_uncertainty_recorded(self):
        process, record = self.exercise([{"kernelStartTicks": 45}, {"kernelStartTicks": 46}], [subprocess.TimeoutExpired("docker", 90)], [None, None, None])
        process.terminate.assert_not_called()
        process.kill.assert_not_called()
        self.assertIn("uncertain", record["state"])
        self.assertIn("leaseExpiresUtc", record)

    def test_cleanup_timeout_still_records_failure_and_identity_checked_kill(self):
        identity = {"kernelStartTicks": 45}
        process, record = self.exercise([identity, identity, identity], [subprocess.TimeoutExpired("docker", 90)] * 3, [None, None, None, None])
        process.terminate.assert_called_once()
        process.kill.assert_called_once()
        self.assertEqual(record["cleanupFailure"], "TimeoutExpired")
        self.assertIn("uncertain", record["state"])


if __name__ == "__main__":
    unittest.main()
