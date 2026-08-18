---
Name: Content search says when it is switched off
Category: Fix
Description: On a deployment without an embedding provider, searching indexed content now answers "indexing is not active here, configure this" instead of failing with an opaque error.
Icon: Search
Order: -20260818
---

# Content search says when it is switched off

Content indexing is optional: a deployment that has no embedding provider configured simply does
not index anything. That was always the intent, but asking for indexed content on such a
deployment did not say so — the `search_chunks` tool failed with a bare "An error occurred", and
the Space settings page could fault the same way. An outage and a switched-off capability looked
identical, so the natural next step was to go hunting for missing data.

Now every content-search surface answers the way it documents: an empty result carrying one line
that says content indexing is not active on this deployment and names the settings to configure.
The chunk viewer on a Document node and the Content Indexing settings tab make the same
distinction rather than erroring.

This is about the message, not the capability: where indexing is configured nothing changes, and
where it is not, configuring an embedding endpoint is still what turns it on. One related fix
comes with it — a deployment using a local, key-less embedding endpoint was being held inert by a
check for an API key it does not use, and now indexes normally.
