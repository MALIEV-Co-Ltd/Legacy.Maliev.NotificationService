# Legacy.Maliev.NotificationService

This repository is the public, sanitized legacy compatibility service for the
email notification endpoints that used to live in `R:\maliev-web` as `Maliev.EmailService`.

## Non-negotiable boundaries

- Keep the original `maliev-web` repository private.
- Do not copy monorepo Git history or legacy configuration/resource files.
- Do not commit Brevo API keys, connection strings, service-account material,
  JWT keys, SMTP credentials, or generated secret-audit evidence.
- Preserve the legacy `/Emails` route prefix and query/form field names until
  consumers are explicitly migrated.
- The legacy service owns no database. Do not add a PostgreSQL dependency unless
  a future audited feature introduces durable notification state.

## Validation

Run from this repository root:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format Legacy.Maliev.NotificationService.slnx --verify-no-changes --no-restore
dotnet list package --vulnerable --include-transitive
gitleaks git . --redact=100 --exit-code 0 --no-banner --no-color
```

## Service conventions

- Runtime: .NET 10.
- OpenAPI UI: Scalar through `Maliev.Aspire.ServiceDefaults`; no Swashbuckle.
- Logging: built-in `ILogger<T>` only; do not reintroduce `Maliev.LoggerService`.
- Auth: legacy `/Emails/*` endpoints remain authenticated.
- Provider: Brevo transactional email through typed options.
- Secrets: runtime-only `Brevo:ApiKey`, sourced from `maliev-legacy-secrets`.
