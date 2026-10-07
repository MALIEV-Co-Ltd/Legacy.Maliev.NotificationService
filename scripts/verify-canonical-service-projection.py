"""Render pinned source locally and validate only the dormant Service projection."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import yaml

APPLICATION = "Legacy.Maliev.NotificationService"
GITOPS_COMMIT = "c9cf2ed4cd7501e989baf6b9af5bf08c87d78893"
PRODUCER_COMMIT = "54781e6ec281de06a7409cfedf1f171a3cb01600"
PRODUCER_HASH = "c3f8664426661f96720d8af08a265bd184c801c3fab47ca292cb3cb920735854"
FILE_PRODUCER_HASHES = {
    "scripts/Get-LegacyFileIdentityPlan.ps1": "5767dc4b841296ed0fce743edb6afea2006259556a5006254b1b39f05570f321",
    "scripts/Invoke-LegacyFileIdentityResourceApplication.ps1": "b3f125d90829ef29d4fb3eb1b311d3f34cab24c0fa2d8575a1836a832c0ea3f3",
}
OVERLAYS = {
    "Legacy.Maliev.DocumentService": "3-apps/_legacy-document-service/overlays/legacy",
    "Legacy.Maliev.NotificationService": "3-apps/_legacy-notification-service/overlays/legacy",
}
MAX_BYTES = 1048576


class ProjectionRejected(RuntimeError):
    def __init__(self):
        super().__init__("Canonical Service source projection rejected; details withheld.")


def require(condition):
    if not condition:
        raise ProjectionRejected()


def command(args):
    result = subprocess.run(args, capture_output=True, timeout=30, check=False)
    require(result.returncode == 0 and len(result.stdout) <= MAX_BYTES)
    return result.stdout


def verify_checkout(path, expected):
    require(command(["git", "-C", str(path), "rev-parse", "HEAD"]).decode().strip() == expected)
    require(command(["git", "-C", str(path), "status", "--porcelain=v1", "--untracked-files=all"]) == b"")


def normalized_hash(path):
    require(path.is_file() and not path.is_symlink())
    return hashlib.sha256(path.read_bytes().replace(b"\r\n", b"\n")).hexdigest()


def verify_file_absence_source(consumer):
    # The accepted recording producer returns ServiceAccount+Deployment only.
    # Refuse any changed producer instead of inferring a new empty Service lane.
    for relative, expected in FILE_PRODUCER_HASHES.items():
        path = consumer / relative
        require(normalized_hash(path) == expected)
        committed = command(["git", "-C", str(consumer), "show", "HEAD:" + relative])
        require(hashlib.sha256(committed).hexdigest() == expected)


class UniqueLoader(yaml.SafeLoader):
    pass


def unique_mapping(loader, node):
    result = {}
    for key_node, value_node in node.value:
        key = loader.construct_object(key_node, deep=True)
        require(type(key) is str and key not in result)
        result[key] = loader.construct_object(value_node, deep=True)
    return result


UniqueLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, unique_mapping)


def documents(data):
    require(type(data) is bytes and 0 < len(data) <= MAX_BYTES)
    depth = 0
    for event in yaml.parse(data):
        require(not isinstance(event, yaml.events.AliasEvent))
        if isinstance(event, (yaml.events.MappingStartEvent, yaml.events.SequenceStartEvent)):
            depth += 1
            require(depth <= 32)
        elif isinstance(event, (yaml.events.MappingEndEvent, yaml.events.SequenceEndEvent)):
            depth -= 1
    values = [value for value in yaml.load_all(data, Loader=UniqueLoader) if value is not None]
    require(0 < len(values) <= 512)
    for value in values:
        require(type(value) is dict and type(value.get("apiVersion")) is str)
        require(type(value.get("kind")) is str and value["kind"] and not value["kind"].endswith("List"))
        metadata = value.get("metadata")
        require(type(metadata) is dict and type(metadata.get("name")) is str and metadata["name"])
        require(type(metadata.get("namespace", "")) is str)
    return values


def project(service_documents, active_documents, gate):
    services = [value for value in service_documents if value["kind"] == "Service"]
    identities = [dict(kind=value["kind"], name=value["metadata"]["name"],
                       namespace=value["metadata"].get("namespace", "")) for value in active_documents]
    require(len({(v["kind"], v["name"], v["namespace"]) for v in identities}) == len(identities))
    return gate(APPLICATION, GITOPS_COMMIT, services, identities)


def load_gate(producer):
    module_path = producer / "scripts/service_manifest_wrapper.py"
    require(normalized_hash(module_path) == PRODUCER_HASH)
    spec = importlib.util.spec_from_file_location("accepted_service_projection", module_path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.verify_canonical_service_projection


def verify_projection(consumer, gitops, producer, kubectl="kubectl"):
    verify_checkout(gitops, GITOPS_COMMIT)
    verify_checkout(producer, PRODUCER_COMMIT)
    gate = load_gate(producer)
    active = documents(command([kubectl, "kustomize", str(gitops / "2-environments/4-legacy")]))
    if APPLICATION == "Legacy.Maliev.FileService":
        verify_file_absence_source(consumer)
        service_documents = []
    else:
        service_documents = documents(command([kubectl, "kustomize", str(gitops / OVERLAYS[APPLICATION])]))
    receipt = project(service_documents, active, gate)
    verify_checkout(gitops, GITOPS_COMMIT)
    verify_checkout(producer, PRODUCER_COMMIT)
    require(normalized_hash(producer / "scripts/service_manifest_wrapper.py") == PRODUCER_HASH)
    if APPLICATION == "Legacy.Maliev.FileService":
        verify_file_absence_source(consumer)
    # Only a small gate receipt is returned. No full render, secrets or workloads.
    return dict(schemaVersion="canonical-service-consumer-source/v1", application=APPLICATION,
                producerCommit=PRODUCER_COMMIT, gitopsCommit=GITOPS_COMMIT,
                originalServiceWrapperPath="Maliev.EmailService.Api/deploy-service.ps1",
                originalSeparationCommit="3a393215d883fa35e1461f69c876bf2ead7ce36e",
                originalCheckpoint="135e526d0dab85c415b3afdcefd7b70fe2c82e2f",
                sourceProjection=receipt, deploymentAllowed=False, runtimeAccepted=False,
                consumerAdoptionAccepted=False)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--consumer", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--gitops", type=Path, required=True)
    parser.add_argument("--producer", type=Path, required=True)
    parser.add_argument("--kubectl", default="kubectl")
    args = parser.parse_args()
    try:
        print(json.dumps(verify_projection(args.consumer, args.gitops, args.producer, args.kubectl), sort_keys=True))
        return 0
    except Exception:
        print(str(ProjectionRejected()), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.dont_write_bytecode = True
    raise SystemExit(main())
