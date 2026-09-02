---
Name: A failed delivery now tells you it failed
Category: Fix
Description: When a very large message could not be delivered, the notification about the failure could itself run out of memory and vanish — leaving the sender waiting on an answer that never came. Reporting a failure, acknowledging a delivery and logging one no longer copy the message.
Icon: Alert
Order: -20260902
---

# A failed delivery now tells you it failed

A companion to [A very large import can no longer take a portal down](../2026-09-02-large-imports-no-longer-take-a-portal-down),
and the half of that story that was still missing.

When a delivery fails, the platform sends a notification back to whoever sent it, so the operation
ends with a real answer instead of waiting out its timeout. On 2 September, on one portal instance,
**that notification failed too** — four times — while a large message was being handled. The result
was the worst possible version of the original problem: the message was lost, and so was every trace
that it had been lost. From the sender's side it had simply never happened.

Three separate things were copying the message when they had no need to.

**The failure report carried the message it was reporting on.** A report about a message too large to
deliver was therefore itself too large to deliver, and died at exactly the wall it was describing.
This had been fixed twice before, each time at one specific place in the code — and this was a third
place that had never been told. It is now a property of the report itself, so it holds everywhere a
report is created rather than only where somebody remembered.

**Sending any message rendered it as text for a diagnostic log — even when that log was switched
off.** The text was built first and discarded afterwards, so a production instance, which does not
write those logs, paid the full cost of producing them for every message it sent. For a very large
message that cost was what ran out of memory. Diagnostic logs are now produced only when something is
listening, and they identify a message rather than reprinting it — so the cost of a log line no
longer depends on the size of the message. Message contents were already marked as "never write this
to a log"; that marking turned out not to be taking effect, and now does.

**Confirmations sent the message back.** Internally, delivering a message returned the whole message
to the sender as its acknowledgement, so every message crossed the network twice — once to be
delivered and once to be confirmed. Nothing ever read the returned copy; only the outcome was used.
Acknowledgements now carry the outcome and not the contents, which halves what a delivery costs
across the whole platform, not only for large messages.

Alongside these, the internal copy step that moves a message between parts of a portal was rebuilt to
stop converting content back and forth between two text formats on the way. It was allocating several
times the size of the message to make a single copy of it; it now makes the copy directly.

What you should notice: a large operation that cannot be delivered fails **loudly and immediately**,
with a report that arrives, and ordinary messaging costs the portal noticeably less memory.

Very large imports are still better split into batches — that remains the real fix for building an
oversized message in the first place, and no amount of care on the delivery side substitutes for it.
