---
Name: Cancelled and errored rounds keep their token usage
Category: Fix
Description: Stopping a round or hitting a provider error no longer drops the tokens it already consumed from usage and cost reporting.
Icon: Money
Order: -20260826
---

# Cancelled and errored rounds keep their token usage

When a conversation round was stopped mid-flight, or failed on a provider error, some providers
only report their token counts in a final message that never arrived — so nothing was recorded at
all, even though the prompt had already been sent and billed. Usage and cost reporting silently
lost that spend.

Cancelled and errored rounds now always record at least an estimate of what was consumed — the
prompt actually sent and the reply actually generated so far — clearly marked as an estimate when
the provider itself never confirmed a count. Nothing is invisible to cost tracking any more.
