import copy
import datetime as dt
import json
import unittest
from unittest.mock import patch
import run_hosted_join as supervisor


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


if __name__ == "__main__":
    unittest.main()
