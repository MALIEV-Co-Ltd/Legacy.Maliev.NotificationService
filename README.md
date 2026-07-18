# Legacy.Maliev.NotificationService

[![PR validation](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.NotificationService/actions/workflows/pr-validation.yml/badge.svg)](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.NotificationService/actions/workflows/pr-validation.yml)
[![Main CI](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.NotificationService/actions/workflows/ci-main.yml/badge.svg)](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.NotificationService/actions/workflows/ci-main.yml)

Temporary .NET 10 compatibility service extracted from `maliev-web`. It preserves
the legacy `Maliev.EmailService` `/Emails` route contract while the new MALIEV
notification stack is developed independently.

Trusted migrated services use the JSON endpoint instead of the legacy query-string routes, keeping recipient addresses, subjects, and message bodies out of URLs and access logs.

## Architecture

The service uses clean dependency direction: `Api` calls `Application`, provider-independent
domain rules live in `Domain`, and the Brevo provider adapter lives in `Data`. It depends only on
the public `Legacy.Maliev.ServiceDefaults` and `Legacy.Maliev.CompatibilityContracts` repositories
during CI and image builds. Compatibility namespaces and email route/payload behavior remain
unchanged.

The legacy service did not own a database. This extraction intentionally removes the old
Identity/EF/NLog/Swagger footprint and keeps provider credentials in runtime configuration
only. The Brevo API key must come from `maliev-legacy-secrets` or equivalent environment
binding, not from source.

## API endpoints

| Purpose | Method | Route | Access |
| --- | --- | --- | --- |
| Info HTML email | `POST` | `/Emails/info` | Authenticated |
| Manufacturing HTML email | `POST` | `/Emails/manufacturing` | Authenticated |
| No-reply HTML email | `POST` | `/Emails/noreply` | Authenticated |
| Support HTML email | `POST` | `/Emails/support` | Authenticated |
| Info plaintext email | `POST` | `/Emails/info-plaintext` | Authenticated |
| Manufacturing plaintext email | `POST` | `/Emails/manufacturing-plaintext` | Authenticated |
| No-reply plaintext email | `POST` | `/Emails/noreply-plaintext` | Authenticated |
| Support plaintext email | `POST` | `/Emails/support-plaintext` | Authenticated |
| Transactional JSON email | `POST` | `/notifications/v1/email/{channel}` | `legacy.notifications.send` permission |
| Scalar UI | `GET` | `/emails/scalar` | Anonymous |

## Runtime boundaries

- Legacy route prefix: `/Emails`
- Scalar: `/emails/scalar`
- Provider: Brevo transactional email
- Delivery resilience: stable idempotency key, 10-second attempt timeout, and at most two retries for network faults, HTTP 408/429, or provider 5xx responses
- Database: none owned by this service
- Secrets: `Brevo:ApiKey` from `maliev-legacy-secrets`
- Preserved request fields: `to`, `subject`, `body`, `replyTo`, `cc`, `bcc`, `files`
- Preserved combined attachment limit: 200 MB
- Modern JSON request fields: `to`, `subject`, `body`, `replyTo`, `cc`, `bcc`; attachments remain on the compatibility routes until they can be referenced through FileService rather than embedded in JSON

Provider diagnostics record only the channel, provider status/failure type, attempt count,
and duration. Recipient addresses, subjects, bodies, attachments, and API keys are never
written to application logs. Brevo `Retry-After` is honored up to the configured five-second
cap, and caller cancellation stops delivery without another attempt.

The dormant `maliev-gitops/3-apps/_legacy-notification-service` path exists separately from the
new implementation and remains absent from the enabled legacy environment. Its image tag stays
`not-published` until owner review is complete.

Image publication is manual-only through `publish-image.yml`. It first runs the complete validation
workflow, requires the `confirm-publication` boolean, builds with pinned public
`Legacy.Maliev.ServiceDefaults` and `Legacy.Maliev.CompatibilityContracts` commits, scans before
publication, and uses OpenID Connect rather than a stored Google service-account key. The repository
variables `LEGACY_GCP_WORKLOAD_IDENTITY_PROVIDER` and
`LEGACY_GCP_ARTIFACT_REGISTRY_SERVICE_ACCOUNT` must reference the approved existing identity before
the workflow can succeed. The workflow publishes only an immutable commit tag; it does not update
GitOps, apply Kubernetes resources, enable the dormant overlay, or deploy to GKE.

## Validate

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format Legacy.Maliev.NotificationService.slnx --verify-no-changes --no-restore
dotnet list package --vulnerable --include-transitive
```
