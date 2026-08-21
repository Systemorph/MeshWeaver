---
Name: Outbound e-mails no longer arrive twice
Category: Fix
Description: Notification e-mails (contact-form enquiries, invitations) were sometimes delivered twice; the sender no longer produces these duplicates.
Icon: Mail
Order: -20260821
---

# Outbound e-mails no longer arrive twice

Notification e-mails — contact-form enquiries, invitations, agent replies — could arrive in your
inbox twice, because the sender occasionally processed the same queued mail more than once. Sends
are now serialized and each mail's state is re-checked immediately before sending, which eliminates
these duplicate deliveries.
