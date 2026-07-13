---
Name: Snowflake storage backend
Category: What's New
Description: MeshWeaver portals can now run on Snowflake as their system of record, with full feature parity to the PostgreSQL backend.
Icon: Sparkle
---

# Snowflake storage backend

MeshWeaver now ships a Snowflake storage backend as a drop-in alternative to PostgreSQL. A
deployment can choose Snowflake as its system of record and keeps everything it had on
Postgres: per-partition schemas, satellite tables, full-text and semantic (vector) search,
cross-partition queries, access control, node version history, and live-updating views.

Because Snowflake has no server-side triggers or push notifications, the backend performs
those duties itself: history, permission projections, and the auth mirror run as part of every
write, and a durable event log propagates changes between portal instances. Deployments on
emulated or reduced-feature endpoints are detected automatically and the backend falls back to
compatible SQL where needed.

Enable it with `AddPartitionedSnowflakePersistence(connectionString)` in place of the
PostgreSQL registration.
