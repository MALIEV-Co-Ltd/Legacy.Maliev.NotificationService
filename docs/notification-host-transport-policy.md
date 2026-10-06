# Notification host transport policy

The committed `InternalHttpWithTrustedEdgeHttps` policy preserves direct internal
HTTP and the registered `/emails/liveness`, `/emails/readiness` and
`/emails/aspire-liveness` probes. Production HTTPS responses use HSTS with the
original default 30-day maximum age. Development does not enable HSTS.

Only exact addresses configured in `ForwardedHeaders:KnownProxies` admit forwarded
transport metadata. The default empty list disables forwarding; it does not mean
trust every peer. Untrusted or unavailable remote addresses cannot supply the
scheme, client address, host or forwarding prefix. IPv4-mapped peers match an
explicit IPv4 proxy address. No CIDR, wildcard or implicit development proxy trust
is enabled by this Notification policy.

The pre-forwarding boundary binds the original remote peer to that allowlist and
clears client-supplied original-scheme markers. A trusted edge must supply exactly
one valid `http` or `https` scheme, and the actual framework forwarder must accept
it. Non-probe requests with insecure, missing, malformed or unproven edge scheme
receive 426 before authentication. The response contains no request-derived
redirect URL. Registered probes remain reachable over HTTP. CORS remains the
existing explicit-origin policy and executes before authentication; preflight
coverage uses the normal Program.

Operator-supplied proxy addresses and edge TLS configuration remain deployment
prerequisites. These tests control TestServer transport metadata with a controlled
HTTP provider response; they establish the application pipeline, not live ingress or TLS
configuration. Internal HTTP must remain confined by deployment networking.
No proxy address, IAM resource or deployment is configured by this change.

The legacy normal Program used HSTS and HTTPS redirection. This explicit policy
retains HSTS and requires HTTPS at admitted edges without reflecting a request
Host into a redirect. Existing CORS, RS256 authentication, provider startup
validation and the 100 MiB request limit remain registered by normal Program.
