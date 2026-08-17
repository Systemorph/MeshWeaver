---
Name: The Cosmos backend can actually be selected
Category: Fix
Description: Selecting Graph:Storage:Type = Cosmos threw at every startup, and live queries on Cosmos never updated. Both are fixed, and a portal now boots, reads, writes and queries against Cosmos.
Icon: PlugConnected
Order: -20260817
---

# The Cosmos backend can actually be selected

Setting `Graph:Storage:Type = Cosmos` failed at every startup with a message that sent you to
check the one setting that was already correct:

> The Cosmos storage module is registered but the selected storage adapter is not Cosmos.
> Either set Graph:Storage:Type to 'Cosmos' or …

The real cause was internal: the storage adapter every consumer resolves is deliberately wrapped
in a chain of write-integrity decorators, and the Cosmos registration was reaching past that with
a plain type cast that could therefore never succeed. It now resolves the raw backend through the
slot that exists for exactly this purpose. The same latent mistake sat in the PostgreSQL and
Snowflake registrations — reachable the moment a deployment selected either through configuration
— and is fixed in all three.

Live queries on Cosmos were also frozen. The adapter announced nothing when a node was written or
deleted, so anything databound to a query — every list, every view that watches for changes — kept
showing its first snapshot forever and only refreshed on a reload. Writes and deletes now publish
to the change feed the reactive layer listens on, the same way the PostgreSQL backend does.

A portal now boots on Cosmos and round-trips node create, read, update, delete and queries through
the normal APIs, covered by a new host-level test suite that runs against the Cosmos emulator.

Worth knowing before choosing it: Cosmos remains a single-container node store. Access-control
filtering on queries, partition provisioning, satellite-table routing, semantic (vector) search and
version history are not implemented there — see the tracking issue for the measured capability
matrix. PostgreSQL stays the backend for production deployments.
