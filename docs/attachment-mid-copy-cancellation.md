# Cancellation after partial attachment copying

The controller owns opened attachment streams and releases them before provider invocation. Existing cancellation coverage used an already-cancelled token; it did not establish cleanup after copying began.

One deterministic controller regression completes the first attachment, writes one byte of the second, then waits on the actual caller token. Cancellation must propagate that token, dispose both opened streams exactly once, leave the third stream unopened, and never invoke the notification service. The fixture uses synthetic in-memory bytes, a five-second entry deadline, a ten-second copy escape deadline and a thirty-second cancellation deadline; its finally block cancels and awaits the send task on every exit. The independent copy deadline also bounds the fixture if a regression stops forwarding the caller token.

This is direct controller coverage, not HTTP disconnect, provider delivery or timing-race proof. Production code, DTOs, routes, authorization and runtime configuration remain unchanged. Expected full suite:271 cases, retaining all270 prior identities. Hosted build, focused ownership cases, full suite and static gates remain required; no local SDK execution.
