# Legacy.Maliev.NotificationService

[![PR validation](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.NotificationService/actions/workflows/pr-validation.yml/badge.svg)](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.NotificationService/actions/workflows/pr-validation.yml)
[![Main CI](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.NotificationService/actions/workflows/ci-main.yml/badge.svg)](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.NotificationService/actions/workflows/ci-main.yml)

Temporary .NET 10 compatibility service extracted from `maliev-web`. It preserves
the legacy `Maliev.EmailService` `/Emails` route contract while the new MALIEV
notification stack is developed independently.

Trusted migrated services use the JSON endpoint instead of the legacy query-string routes, keeping recipient addresses, subjects, and message bodies out of URLs and access logs.

## Architecture

The service uses clean dependency direction: `Api` calls `Application`, provider-independent
domain rules live in `Domain`, and the Brevo provider adapter lives in `Data`. It depends on
the public MALIEV Aspire source repository during CI and image builds, so no private package
credentials are required.

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
- Database: none owned by this service
- Secrets: `Brevo:ApiKey` from `maliev-legacy-secrets`
- Preserved request fields: `to`, `subject`, `body`, `replyTo`, `cc`, `bcc`, `files`
- Preserved combined attachment limit: 200 MB
- Modern JSON request fields: `to`, `subject`, `body`, `replyTo`, `cc`, `bcc`; attachments remain on the compatibility routes until they can be referenced through FileService rather than embedded in JSON

Deployment is intentionally validation-only until a dedicated
`legacy-maliev-notification` Workload Identity Federation provider and
`maliev-gitops/3-apps/_legacy-notification-service` manifest path exist. Any existing
`maliev-notification-service` GitOps path is reserved for the new implementation and must
not be overwritten by this legacy compatibility service.

## Validate

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format Legacy.Maliev.NotificationService.slnx --verify-no-changes --no-restore
dotnet list package --vulnerable --include-transitive
```
