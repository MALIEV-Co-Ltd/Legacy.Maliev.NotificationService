# Legacy email HTTP follow-up

The accepted PR45/main `45991d85bc87b1fd95a49225d36608a96693252e`
already implements all eight legacy routes, Production RS256 permission checks,
raw UTF-8 bodies, multipart uploads, Brevo serialization, XML documentation and
fixed-origin redirect protection. This slice adds four missing acceptance cases:
missing plaintext recipient, subject or body must return the legacy field error
without a provider request; two attachments must retain their order and exact
binary contents. Only remote provider HTTP is controlled. No mail is delivered.

The tests characterize existing behavior and passed before any runtime edit.
The local formatter normalized workspace line endings in existing callbacks;
the Git-normalized production files have no diff. Runtime behavior is unchanged.

Source checkpoint, observed before and after inspection:
`135e526d0dab85c415b3afdcefd7b70fe2c82e2f` (1,126 commits, no new commits).
Full source records remain in the canonical local MigrationTracking ledger.

| Source SHA | Parent | Accepted portion |
| --- | --- | --- |
| `5fac706a7983a6d359b39acbd670e6800afe020e` | root | Email controller HTTP contracts only; other initial-commit owners remain pending. |
| `54a80f9a23386f5050dd8519b8cd68d7972ad33a` | `957c32ce00b1247f5a9e51d1d24b78c0cfe38337` | Existing typed Brevo transport and channel sender mapping receive joined HTTP proof. |
| `f395d5cc09567767e9e7e2dfa4dc8887ed5a97f8` | `54a80f9a23386f5050dd8519b8cd68d7972ad33a` | Existing Brevo payload behavior receives joined HTTP proof; no runtime credentials copied. |
| `72eb9f1949176392141951d35e6e06f7c30af4c2` | `023aa2f143fa5a8f557d72c1302b9add64792d6a` | Raw-body controller binding and required-field errors only; other source paths remain unresolved. |

Validation: Release builds with warnings as errors, focused HTTP acceptance,
unfiltered suite including disposable PostgreSQL intent tests, raw production
coverage including generated lines, solution formatting, transitive vulnerability
audit, current-tree/JWT-resource and committed-history credential scans.
Local results: 20 focused and 156 full tests, zero failures/skips; zero build
warnings/errors. Raw coverage: API 444/554 (80.14%), Application 171/180 (95.00%),
Data 436/452 (96.46%), Domain 14/14 (100%). No coverage exclusions or threshold
changes. Protected PR and post-merge main CI are separate final admission gates.

Issue44 is the bounded HTTP acceptance issue. Delivery-intent reconciliation
issue26 and overall source migration/data/Aspire acceptance remain separate.
No database refresh, persistent schema changes, deployment or cutover is part
of this slice. Operational data proof belongs exclusively to the data chat.
