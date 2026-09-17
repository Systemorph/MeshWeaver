---
Name: A cold read no longer says a node is missing for the rest of the day
Category: Fix
Description: A query answered while a portal was still warming up could cache "there is nothing there" for the life of that process, so a node that existed read as absent to everything that asked afterwards. The framework now tells "nobody answered yet" from "there is nothing", and keeps only the second.
Icon: DatabaseSearch
Order: -20260917
---

# A cold read no longer says a node is missing for the rest of the day

A portal keeps one live query per question it has been asked, and replays that query's first answer
to everyone who asks again — which is what makes reading the same thing twice cheap. The problem was
what counted as an answer. While a portal is still warming up, a storage backend can finish a query
without saying anything at all; the platform treats that as "nothing matched" so the page renders
instead of hanging, and that fabricated empty then became the cached answer. Nothing ever corrected
it, because the event that refreshes a query is a change to a matching node, and re-writing a node
that is already correct changes nothing.

The visible effect was a restarted portal insisting, for as long as it ran, that records it had
itself stored were missing: access grants read as absent, settings read as unset, a reconcile
reporting that its own writes had not been saved.

A query result now names the backends that never answered, the platform keeps only answers somebody
actually gave, and the next read asks again. An answer everybody gave is cached exactly as before.
