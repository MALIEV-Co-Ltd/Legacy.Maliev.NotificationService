# Concurrent notification intent admission

Fresh-main run 37473185478 executed 269 tests: 268 passed and the existing concurrent admission test failed with PostgreSQL 23505 on AK_NotificationIntent_BusinessEffect. All four attachment stream cases passed. This is a store admission race, not provider execution evidence.

The insert now waits on either reviewed uniqueness boundary. The locked intent lookup still verifies the complete identity, retained key, binding version and digest before returning authority. If no intent exists, only an exact six-field business-effect owner produces conflict; an unknown uniqueness collision remains unavailable. Transaction ambiguity, cancellation and fencing behavior remain unchanged.

The existing real PostgreSQL concurrency case now overlaps both inserts at the command boundary with a 15-second barrier and a 30-second caller timeout. Its sole-fence and unknown-outcome no-resend assertions remain. One additional disposable-database test adds an unexpected unique index and verifies unavailable, no new authority and the unchanged original winner. No schema migration or persistent database change is included.

Native validation remains pending source review. Expected full suite: 270 cases, retaining all 269 prior test identities. Local .NET execution is excluded by the native-first lane.
