---
Name: An interrupted health check no longer blames the database
Category: Fix
Description: When a health probe gave up waiting, the portal recorded it as "the database is unhealthy" — a verdict it had never actually obtained — and opened an engineering ticket about a database that was fine.
Icon: PlugConnected
Order: -20260811
---

# An interrupted health check no longer blames the database

A portal that has just been updated spends its first minute rebuilding everything it serves. While
that is going on it is asked, every ten seconds, whether it is ready yet — and one of those checks
asks the database which version of the schema it holds. On a busy first minute that question
sometimes has to queue for a free connection, and the asker occasionally gives up before it is
answered.

Giving up is not an answer. The check had learned nothing at all about the database — it had not
even opened a connection yet — but it reported "unhealthy" anyway, attached the interruption as
though it were a database error, and that report was written down at the severity that opens an
engineering ticket. So a routine restart, on a portal that then served normally for hours, produced
a ticket about a database outage that never happened.

Now the two are kept apart. If the database itself refuses, cannot be reached, or answers with the
wrong schema version, that is still reported as unhealthy, in full, exactly as before — that is the
whole point of the check, and it is what catches a portal started against a half-migrated database.
But an interruption raised by whoever asked is passed back as an interruption, which is what the
underlying framework expects and already knows how to ignore. No verdict is invented from a
question that was never answered.
