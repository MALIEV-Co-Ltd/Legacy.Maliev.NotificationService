# Dormant canonical Service source validation

Normal PR and main CI render read-only committed GitOps source for Legacy.Maliev.NotificationService
and pass its Service projection plus active namespace identities to the accepted
Workflows gate. The existing native validation workflows are retained.

GitOps source is pinned to `c9cf2ed4cd7501e989baf6b9af5bf08c87d78893`;
the gate is pinned to `54781e6ec281de06a7409cfedf1f171a3cb01600` and its module
hash is checked before import and after rendering. Both dependency checkouts must
have the exact HEAD and no tracked or untracked changes before and after rendering.
The local renderer only calls `kubectl kustomize`; it has no apply or server API path.
YAML aliases, duplicate keys, list envelopes, missing identities and oversized or
deep input fail closed. Full rendered resources are never logged or uploaded.

File has no canonical Service overlay. Its empty Service list is admitted only
while the actual committed File identity-plan and recording-protocol scripts match
the accepted hashes; those scripts emit ServiceAccount and Deployment only.
Document renders its existing overlay, which has no Service. Notification must
render the exact internal ClusterIP Service with named HTTP target port and legacy
labels. An owned identity in the active namespace rejects dormant admission.

This adapts the original Service-only split at
`3a393215d883fa35e1461f69c876bf2ead7ce36e` and the checked-status obligation at
`135e526d0dab85c415b3afdcefd7b70fe2c82e2f` to the canonical consumer source.
The exact original producer path for this consumer is `Maliev.EmailService.Api/deploy-service.ps1`.
Historical NodePort exposure is deliberately replaced by the dormant internal
Service contract; success is source consistency, not runtime parity. Each consumer
keeps its own application identity and original source association.

Receipt deployment, runtime and adoption flags remain false. A successful source
check does not deploy or activate resources, authenticate a provider snapshot,
accept IAM/secrets/resources, or replace existing native tests. No cluster context,
credentials, infrastructure writes, Argo sync or publication are needed.

For local verification, use Python with PyYAML 6.0.3, clean pinned dependency
checkouts, and `kubectl` available on PATH. Set `CANONICAL_SERVICE_PRODUCER` to
the pinned Workflows checkout, compile the two Python modules, run
`python -B -W error -m unittest discover -s scripts -p test_canonical_service_projection.py -v`,
then run `scripts/verify-canonical-service-projection.py --gitops <checkout> --producer <checkout>`.
Changing a dependency or File producer requires a reviewed hash/pin update and
matching regression validation; floating main is not accepted.
