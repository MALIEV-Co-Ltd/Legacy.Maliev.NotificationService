# Legacy email host request limit

Final private source checkpoint 135e526d0dab85c415b3afdcefd7b70fe2c82e2f
configures Kestrel's total request body limit to 100 MiB in Maliev.EmailService.Api/Program.cs.
Accepted Notification51 preserves the controller's aggregate attachment100MiB
rule, but its Program and pinned shared host7edcd961 lack the source host setting.
TestServer attachment tests did not establish real Kestrel acceptance.

The normal Program now sets the Kestrel limit from the retained controller constant.
This preserves two distinct source constraints: total request bytes, including
multipart overhead, and aggregate file bytes. No multipart/form limits are raised.

Five new tests use normal Program with WebApplicationFactory.UseKestrel(0), real
loopback HTTP and Production RS256 authentication. Only remote Brevo transport
is controlled. They inspect actual Kestrel options; send32MiB and exact100MiB raw
bodies; preserve32MiB multipart bytes; and reject declared-over-cap multipart
before provider invocation. The collection runs without parallel large-body tests.
No live provider send, ingress capacity, deployment or source whole closure is
claimed. Native Release/full expected220 and raw four-assembly floors remain
required; this draft has not run native tests.

The75-assignment remaining source readback is off-repository. Source resources,
private configuration and history were not copied. Historical absent XML/Startup
paths still need explicit supersession review; a same-name target is not acceptance.
