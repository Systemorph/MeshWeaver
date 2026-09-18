---
Name: A new instance gets its own certificate
Category: Fix
Description: >-
  Provisioning an instance now asks for its TLS certificate, waits until it is really issued, and
  refuses to call the instance ready while its host is served another instance's certificate.
Icon: ShieldCheckmark
Order: -20260915
---

# A new instance gets its own certificate

An instance's ingress names the TLS secret its host is served from. Until now, *asking* for that
certificate was a hand-written annotation that lived in a checked-in values file — while a
provisioning run renders its values from the deployment record. A record without the annotation
produced an ingress that named a secret nobody issued, and the controller answered every visitor
with the fallback: **another instance's certificate**, which every browser refuses. The portal
behind it was perfectly healthy, which is what made it hard to see.

Three changes make that shape unreachable:

- **The issuer is part of the record**, and it is there by default. Declaring an instance with a
  host and a TLS secret is enough; nobody has to remember an annotation. A record that names its
  own issuer keeps it, and `none` says "this secret is created by other means" out loud.
- **The chart refuses to render** an ingress that names a TLS secret with no issuer behind it —
  the render stops and names the missing value instead of shipping a host that cannot be opened.
- **The run does not finish early.** The certificate step waits for the certificate to exist, not
  for the request to be accepted, and the verification step now checks the certificate the host
  actually serves. A wrong certificate is reported as a wrong certificate, rather than as thirty
  failed attempts that read like a dead application.
