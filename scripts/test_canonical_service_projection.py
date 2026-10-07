import copy
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("consumer_projection", Path(__file__).with_name("verify-canonical-service-projection.py"))
consumer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(consumer)
PRODUCER = Path(os.environ["CANONICAL_SERVICE_PRODUCER"])


class ProjectionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.gate = staticmethod(consumer.load_gate(PRODUCER))
        cls.fixture = json.loads((PRODUCER / "tests/fixtures/canonical-service-gate/dormant-projection.json").read_bytes())

    def projection(self):
        services = [copy.deepcopy(self.fixture["notificationService"])] if consumer.APPLICATION.endswith("NotificationService") else []
        active = [dict(apiVersion="v1", kind=v["kind"], metadata=dict(name=v["name"], namespace=v["namespace"])) for v in self.fixture["activeIdentities"]]
        return services, active

    def test_actual_accepted_projection(self):
        receipt = consumer.project(*self.projection(), self.gate)
        self.assertTrue(receipt["dormantProjectionConsistent"])
        self.assertFalse(receipt["deploymentAllowed"])
        self.assertFalse(receipt["runtimeAccepted"])
        self.assertFalse(receipt["consumerAdoptionAccepted"])

    def test_active_owned_identity_rejected(self):
        services, active = self.projection()
        name = {"Legacy.Maliev.FileService": "legacy-maliev-file", "Legacy.Maliev.DocumentService": "legacy-maliev-document-service", "Legacy.Maliev.NotificationService": "legacy-maliev-notification-service"}[consumer.APPLICATION]
        active.append(dict(apiVersion="v1", kind="Deployment", metadata=dict(name=name, namespace="maliev-legacy")))
        with self.assertRaises(RuntimeError):
            consumer.project(services, active, self.gate)

    def test_duplicate_active_identity_rejected(self):
        services, active = self.projection()
        active.append(copy.deepcopy(active[0]))
        with self.assertRaises(RuntimeError):
            consumer.project(services, active, self.gate)

    def test_service_exposure_or_unexpected_lane_rejected(self):
        services, active = self.projection()
        services = [copy.deepcopy(self.fixture["notificationService"])]
        services[0]["spec"]["type"] = "LoadBalancer"
        with self.assertRaises(RuntimeError):
            consumer.project(services, active, self.gate)

    def test_service_selector_or_unexpected_lane_rejected(self):
        services, active = self.projection()
        services = [copy.deepcopy(self.fixture["notificationService"])]
        services[0]["spec"]["selector"] = {"app": "foreign"}
        with self.assertRaises(RuntimeError):
            consumer.project(services, active, self.gate)

    def test_service_port_or_unexpected_lane_rejected(self):
        services, active = self.projection()
        services = [copy.deepcopy(self.fixture["notificationService"])]
        services[0]["spec"]["ports"][0]["port"] = "8080"
        with self.assertRaises(RuntimeError):
            consumer.project(services, active, self.gate)

    def test_duplicate_yaml_key_rejected(self):
        with self.assertRaises(RuntimeError):
            consumer.documents(b"kind: Service\nkind: Deployment\n")

    def test_alias_yaml_rejected(self):
        with self.assertRaises(RuntimeError):
            consumer.documents(b"a: &value [1]\nb: *value\n")

    def test_list_envelope_rejected(self):
        with self.assertRaises(RuntimeError):
            consumer.documents(b"apiVersion: v1\nkind: List\nmetadata: {name: hidden}\nitems: []\n")

    def test_missing_identity_rejected(self):
        with self.assertRaises(RuntimeError):
            consumer.documents(b"apiVersion: v1\nkind: Service\nmetadata: {}\n")

    def test_empty_yaml_rejected(self):
        with self.assertRaises(RuntimeError):
            consumer.documents(b"---\n")

    def test_oversized_yaml_rejected(self):
        with self.assertRaises(RuntimeError):
            consumer.documents(b"x" * (consumer.MAX_BYTES + 1))

    def test_excessive_depth_rejected(self):
        with self.assertRaises(RuntimeError):
            consumer.documents(b"x: " + b"[" * 33 + b"0" + b"]" * 33)

    def test_nonstring_mapping_key_rejected(self):
        with self.assertRaises(RuntimeError):
            consumer.documents(b"1: invalid\n")

    def test_nonzero_render_does_not_admit(self):
        with patch.object(consumer.subprocess, "run", return_value=subprocess.CompletedProcess([], 9, b"provider-bytes", b"secret")):
            with self.assertRaises(RuntimeError) as error:
                consumer.command(["kubectl", "kustomize", "fixture"])
            self.assertNotIn("secret", str(error.exception))

    def test_command_has_finite_timeout(self):
        with patch.object(consumer.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, b"ok", b"")) as run:
            self.assertEqual(b"ok", consumer.command(["git", "rev-parse", "HEAD"]))
            self.assertEqual(30, run.call_args.kwargs["timeout"])

    def test_wrong_source_head_rejected(self):
        with patch.object(consumer, "command", return_value=b"0" * 40):
            with self.assertRaises(RuntimeError):
                consumer.verify_checkout(Path("fixture"), consumer.GITOPS_COMMIT)

    def test_dirty_source_rejected(self):
        with patch.object(consumer, "command", side_effect=[consumer.GITOPS_COMMIT.encode(), b" M source.yaml\n"]):
            with self.assertRaises(RuntimeError):
                consumer.verify_checkout(Path("fixture"), consumer.GITOPS_COMMIT)

    def test_wrong_producer_module_rejected(self):
        with patch.object(consumer, "normalized_hash", return_value="0" * 64):
            with self.assertRaises(RuntimeError):
                consumer.load_gate(PRODUCER)

    def test_file_absence_requires_actual_unchanged_source(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            (path / "scripts").mkdir()
            for name in consumer.FILE_PRODUCER_HASHES:
                (path / name).write_text("Service-shaped substitute")
            with self.assertRaises(RuntimeError):
                consumer.verify_file_absence_source(path)

    def test_after_render_source_change_rejected(self):
        with patch.object(consumer, "verify_checkout", side_effect=[None, None, consumer.ProjectionRejected()]), \
             patch.object(consumer, "load_gate", return_value=self.gate), \
             patch.object(consumer, "documents", side_effect=[self.projection()[1], self.projection()[0]]), \
             patch.object(consumer, "command", return_value=b"render"), \
             patch.object(consumer, "verify_file_absence_source"):
            with self.assertRaises(RuntimeError):
                consumer.verify_projection(Path("consumer"), Path("gitops"), PRODUCER)


if __name__ == "__main__":
    unittest.main()
