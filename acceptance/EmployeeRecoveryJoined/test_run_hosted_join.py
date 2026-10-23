import copy
import datetime as dt
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch
import run_hosted_join as supervisor


class SourceBindingTests(unittest.TestCase):
    def test_exact_lf_blob_passes_and_crlf_or_changed_source_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source.cs"
            raw = b"class Source {}\n"
            source.write_bytes(raw)
            head, tree = "a" * 40, "b" * 40
            bindings = {"candidateHeads": {"auth": {"head": head, "tree": tree}},
                        "files": [{"repo": "auth", "path": source.name,
                                   "sha256": hashlib.sha256(raw).hexdigest()}]}
            (root / "source-bindings.json").write_text(json.dumps(bindings))
            def command(args):
                if args[-1] == "HEAD":
                    return head
                if args[-1] == "HEAD^{tree}":
                    return tree
                return ""
            with patch.object(supervisor, "PACKET", root), patch.object(supervisor, "command", side_effect=command):
                self.assertEqual(head, supervisor.verify_sources({"auth": root}, {"auth": head})["auth"]["head"])
                for altered in (b"class Source {}\r\n", b"class Changed {}\n"):
                    source.write_bytes(altered)
                    with self.assertRaisesRegex(ValueError, "Reviewed source file mismatch"):
                        supervisor.verify_sources({"auth": root}, {"auth": head})

    def test_unreviewed_producer_head_fails_before_checkout_inspection(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "source-bindings.json").write_text(json.dumps({
                "candidateHeads": {"auth": {"head": "a" * 40, "tree": "b" * 40}}, "files": []}))
            with patch.object(supervisor, "PACKET", root), patch.object(supervisor, "command") as command:
                with self.assertRaisesRegex(ValueError, "reviewed published head"):
                    supervisor.verify_sources({"auth": root}, {"auth": "c" * 40})
                command.assert_not_called()


class CleanupBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.started = dt.datetime.now(dt.timezone.utc) - dt.timedelta(seconds=10)
        self.value = {
            "Id": "a" * 64, "Created": (self.started + dt.timedelta(seconds=1)).isoformat(),
            "Config": {"Image": "postgres:18-alpine", "Labels": {
                supervisor.LABEL: "run", "maliev.validation.notification62.fixture": "b" * 32}},
            "Mounts": [{"Type": "tmpfs"}],
            "HostConfig": {"Memory": 1024**3, "NanoCpus": 1_000_000_000,
                           "PortBindings": {"5432/tcp": [{"HostIp": "127.0.0.1", "HostPort": "54321"}]}}
        }

    def test_exact_disposable_container_is_eligible(self):
        self.assertEqual("a" * 64, supervisor.validate_container(self.value, "run", self.started)["id"])

    def test_persistent_volume_preserved(self):
        self.value["Mounts"][0]["Type"] = "volume"
        with self.assertRaisesRegex(RuntimeError, "persistent"):
            supervisor.validate_container(self.value, "run", self.started)

    def test_other_run_preserved(self):
        with self.assertRaisesRegex(RuntimeError, "ownership"):
            supervisor.validate_container(self.value, "other", self.started)

    def test_untracked_fixture_preserved(self):
        self.value["Config"]["Labels"]["maliev.validation.notification62.fixture"] = "other"
        with self.assertRaisesRegex(RuntimeError, "ownership"):
            supervisor.validate_container(self.value, "run", self.started)

    def test_old_container_preserved(self):
        self.value["Created"] = (self.started - dt.timedelta(seconds=1)).isoformat()
        with self.assertRaisesRegex(RuntimeError, "creation"):
            supervisor.validate_container(self.value, "run", self.started)

    def test_different_image_preserved(self):
        self.value["Config"]["Image"] = "other:latest"
        with self.assertRaisesRegex(RuntimeError, "image"):
            supervisor.validate_container(self.value, "run", self.started)

    def test_non_loopback_preserved(self):
        self.value["HostConfig"]["PortBindings"]["5432/tcp"][0]["HostIp"] = "0.0.0.0"
        with self.assertRaisesRegex(RuntimeError, "loopback"):
            supervisor.validate_container(self.value, "run", self.started)

    def test_missing_memory_limit_preserved(self):
        self.value["HostConfig"]["Memory"] = 0
        with self.assertRaisesRegex(RuntimeError, "limits"):
            supervisor.validate_container(self.value, "run", self.started)

    def test_cpu_over_limit_preserved(self):
        self.value["HostConfig"]["NanoCpus"] = 2_000_000_000
        with self.assertRaisesRegex(RuntimeError, "limits"):
            supervisor.validate_container(self.value, "run", self.started)

    def test_redis_requires_its_own_memory_and_cpu_limits(self):
        self.value["Config"]["Image"] = "redis:7-alpine"
        self.value["HostConfig"].update(Memory=128 * 1024 * 1024, NanoCpus=500_000_000)
        supervisor.validate_container(self.value, "run", self.started)
        for key, over in (("Memory", 128 * 1024 * 1024 + 1), ("NanoCpus", 500_000_001)):
            with self.subTest(limit=key):
                changed = copy.deepcopy(self.value)
                changed["HostConfig"][key] = over
                with self.assertRaisesRegex(RuntimeError, "limits"):
                    supervisor.validate_container(changed, "run", self.started)

    def test_pid_reuse_does_not_signal(self):
        identity = {"pid": 12, "pgrp": 12, "birthTicks": 20, "executable": "dotnet"}
        changed = copy.copy(identity)
        changed["birthTicks"] = 99
        with patch.object(supervisor, "proc_identity", return_value=changed), patch.object(supervisor.os, "kill") as kill:
            with self.assertRaisesRegex(RuntimeError, "identity changed"):
                supervisor.signal_exact(identity, identity, "run", 15)
            kill.assert_not_called()

    def test_exited_process_does_not_signal(self):
        with patch.object(supervisor, "proc_identity", return_value=None), patch.object(supervisor.os, "kill") as kill:
            supervisor.signal_exact({"pid": 12}, {}, "run", 15)
            kill.assert_not_called()

    def test_unowned_group_does_not_signal(self):
        member = {"pid": 13, "pgrp": 90, "birthTicks": 20, "executable": "dotnet"}
        leader = {"pid": 12, "pgrp": 12, "birthTicks": 10, "executable": "dotnet"}
        with patch.object(supervisor, "proc_identity", return_value=member), patch.object(supervisor.os, "kill") as kill:
            with self.assertRaisesRegex(RuntimeError, "ownership"):
                supervisor.signal_exact(member, leader, "run", 15)
            kill.assert_not_called()

    def test_one_preserved_container_does_not_block_other_owned_cleanup(self):
        blocked = copy.deepcopy(self.value)
        blocked['Id'] = 'c' * 64
        blocked['Mounts'][0]['Type'] = 'volume'
        removed = self.value['Id']
        observed = []
        def fake_command(args):
            observed.append(args)
            if '--filter' in args:
                return blocked['Id'] + '\n' + removed
            if 'inspect' in args:
                return json.dumps([blocked if args[-1] == blocked['Id'] else self.value])
            if 'ls' in args:
                return blocked['Id']
            return args[-1]
        with patch.object(supervisor, 'command', side_effect=fake_command):
            with self.assertRaises(supervisor.ContainerCleanupError) as raised:
                supervisor.cleanup_containers('run', self.started)
        self.assertEqual(removed, raised.exception.receipts[0]['id'])
        self.assertEqual(blocked['Id'], raised.exception.failures[0]['id'])
        self.assertFalse(any('stop' in args and args[-1] == blocked['Id'] for args in observed))

    def test_expired_native_lease_does_not_launch(self):
        with patch.object(supervisor.subprocess, 'Popen') as launch:
            with self.assertRaises(TimeoutError):
                supervisor.run_native([], {}, None, 'test', 'run', deadline=-1)
            launch.assert_not_called()

    def test_missing_leader_closes_unreleased_gate_and_reaps_helper(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            (output / "run-owner.json").write_text(json.dumps({"run": "run", "stages": []}))
            process = Mock(pid=12)
            process.wait.return_value = 125
            with patch.object(supervisor, "admit", return_value={}), \
                 patch.object(supervisor.subprocess, "Popen", return_value=process), \
                 patch.object(supervisor, "proc_identity", return_value=None), \
                 patch.object(supervisor, "group_members", return_value=[]):
                with self.assertRaisesRegex(RuntimeError, "custody unavailable"):
                    supervisor.run_native(["dotnet", "build"], {}, output, "build", "run")
            process.stdin.write.assert_not_called()
            process.stdin.close.assert_called()
            process.wait.assert_called()
            receipt = json.loads((output / "build-custody.json").read_text())
            self.assertTrue(receipt["groupAbsent"])
            self.assertFalse(receipt["gateReleased"])

    def test_cleanup_preserves_containers_when_planned_stage_receipt_is_missing(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            (output / "run-owner.json").write_text(json.dumps({
                "run": "run", "started": self.started.isoformat(), "stages": ["build"]}))
            with patch.object(supervisor.sys, "argv", ["run_hosted_join.py", "--cleanup-only", directory]), \
                 patch.object(supervisor, "Path", type(output)), \
                 patch.object(supervisor.os, "name", "posix"), \
                 patch.dict(supervisor.os.environ, {"GITHUB_ACTIONS": "true"}), \
                 patch.object(supervisor, "cleanup_containers", return_value=[]) as cleanup:
                with self.assertRaisesRegex(RuntimeError, "custody"):
                    supervisor.main()
                cleanup.assert_not_called()

    def test_sdk_gate_rejects_eof_and_only_executes_after_explicit_release(self):
        with patch.object(supervisor.os, "read", return_value=b""), \
             patch.object(supervisor.os, "execvpe") as execute:
            with self.assertRaises(SystemExit) as result:
                exec(supervisor.SDK_GATE, {})
            self.assertEqual(125, result.exception.code)
            execute.assert_not_called()
        with patch.object(supervisor.os, "read", return_value=b"1"), \
             patch.object(supervisor.os, "execvpe") as execute, \
             patch.object(supervisor.sys, "argv", ["-c", "dotnet", "build"]):
            exec(supervisor.SDK_GATE, {})
            execute.assert_called_once_with("dotnet", ["dotnet", "build"], supervisor.os.environ)

    def test_gate_release_follows_durable_stage_and_identity_custody(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            (output / "run-owner.json").write_text(json.dumps({"run": "run", "stages": []}))
            process = Mock(pid=12)
            process.wait.return_value = 0
            identity = {"pid": 12, "pgrp": 12, "birthTicks": 20, "executable": "python3"}
            def release(value):
                self.assertEqual(b"1", value)
                self.assertEqual(["build"], json.loads((output / "run-owner.json").read_text())["stages"])
                self.assertEqual(identity, json.loads((output / "build-custody.json").read_text())["leader"])
            process.stdin.write.side_effect = release
            with patch.object(supervisor, "admit", return_value={}), \
                 patch.object(supervisor.subprocess, "Popen", return_value=process) as launch, \
                 patch.object(supervisor, "proc_identity", return_value=identity), \
                 patch.object(supervisor, "quiesce"), \
                 patch.object(supervisor, "group_members", return_value=[]):
                supervisor.run_native(["dotnet", "build"], {}, output, "build", "run")
            process.stdin.write.assert_called_once()
            self.assertEqual([supervisor.sys.executable, "-c", supervisor.SDK_GATE, "dotnet", "build"], launch.call_args.args[0])
            self.assertTrue(launch.call_args.kwargs["start_new_session"])
            self.assertTrue(json.loads((output / "build-custody.json").read_text())["groupAbsent"])

    def test_failed_identity_receipt_never_releases_sdk_gate(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            (output / "run-owner.json").write_text(json.dumps({"run": "run", "stages": []}))
            process = Mock(pid=12)
            process.wait.return_value = 125
            identity = {"pid": 12, "pgrp": 12, "birthTicks": 20, "executable": "python3"}
            write_text = Path.write_text
            failed = False
            def write(path, value, *args, **kwargs):
                nonlocal failed
                if path.name == "build-custody.json" and '"leader"' in value and not failed:
                    failed = True
                    raise OSError("Custody write failed")
                return write_text(path, value, *args, **kwargs)
            with patch.object(supervisor, "admit", return_value={}), \
                 patch.object(supervisor.subprocess, "Popen", return_value=process), \
                 patch.object(supervisor, "proc_identity", return_value=identity), \
                 patch.object(Path, "write_text", write), \
                 patch.object(supervisor, "group_members", return_value=[]):
                with self.assertRaisesRegex(OSError, "Custody write"):
                    supervisor.run_native(["dotnet", "build"], {}, output, "build", "run")
            process.stdin.write.assert_not_called()
            process.stdin.close.assert_called()
            process.wait.assert_called()

    def test_unverified_leader_or_remaining_group_blocks_container_cleanup(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            owner = {"run": "run", "stages": ["build"]}
            (output / "build-custody.json").write_text(json.dumps({"groupAbsent": False, "gateReleased": False}))
            with self.assertRaisesRegex(RuntimeError, "unverified"):
                supervisor.require_native_absence(output, owner)
            (output / "build-custody.json").write_text(json.dumps({"leader": {"pgrp": 12}, "groupAbsent": True}))
            with patch.object(supervisor, "group_members", return_value=[{"pid": 12}]):
                with self.assertRaisesRegex(RuntimeError, "remain"):
                    supervisor.require_native_absence(output, owner)

    def test_unconfirmed_gate_helper_exit_preserves_unknown_custody(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            (output / "run-owner.json").write_text(json.dumps({"run": "run", "stages": []}))
            process = Mock(pid=12)
            process.wait.side_effect = supervisor.subprocess.TimeoutExpired("gate", 5)
            with patch.object(supervisor, "admit", return_value={}), \
                 patch.object(supervisor.subprocess, "Popen", return_value=process), \
                 patch.object(supervisor, "proc_identity", return_value=None):
                with self.assertRaises(supervisor.subprocess.TimeoutExpired):
                    supervisor.run_native(["dotnet", "build"], {}, output, "build", "run")
            process.stdin.write.assert_not_called()
            with self.assertRaisesRegex(RuntimeError, "unverified"):
                supervisor.require_native_absence(output, json.loads((output / "run-owner.json").read_text()))


if __name__ == "__main__":
    unittest.main()
