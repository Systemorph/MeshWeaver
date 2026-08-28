---
Name: Bodyless red-log captures no longer become undiagnosable incidents
Category: Fix
Description: A red log line that arrived with no message, exception or stack trace could open an incident that names a component and no defect; the portal now files it as a capture gap instead, whatever version of the log watcher sent it.
Icon: Sparkle
Order: -20260828
---

# Bodyless red-log captures no longer become undiagnosable incidents

The red-log ticketing pipeline could open an incident — and a GitHub issue — whose entire
content was a console header such as `OrleansRoutingService[0]`: no message, no exception, no
stack trace, nothing an engineer could act on. Every later bodyless capture from the same
component then folded into that one bucket instead of being noticed. The watcher that reads the
logs had been fixed to hold such lines back, but the watcher is a separately shipped component
and the running one predated the fix.

The portal now checks every incoming report itself. A report that carries no diagnostic is
refused and recorded on the per-namespace "capture gap" finding — the same one an up-to-date
watcher files — with the original component and fingerprint kept as evidence, so the incident
list shows where bodyless captures are coming from rather than a ticket nobody can diagnose.
