---
Name: A new instance no longer waits forever for its database
Category: Fix
Description: >-
  An instance provisioned from its deployment record, with its database connection string kept in
  Key Vault, never started: its start-up gate waited for a built-in database server that such an
  instance does not have. It now waits for the database server the record names.
Icon: Timer
Order: -20260915
---

# A new instance no longer waits forever for its database

Before a Memex portal starts, a small **start-up gate** waits until its database accepts
connections, so the portal never races an empty server. It has to know *which* server to wait for.

An instance **provisioned from its deployment record** keeps its database connection string —
password included — in **Key Vault**, where the cluster reads it at start-up. The record states the
server's address separately. The gate, however, looked for the address inside the connection string
in the chart's own settings, found only the built-in default there (a database server that runs
inside the cluster for self-hosted installs), and waited for *that*. Such an instance runs no
built-in server, so the gate waited forever: the new instance showed as starting, and never became
ready.

## What changed

- **The gate now waits for the server the record names** whenever the connection string comes from
  Key Vault rather than from the chart's settings. Instances that put the connection string in their
  settings, and self-hosted installs with the built-in server, wait for exactly what they did before.
- **An install that names no reachable server is refused up front.** If an instance uses an
  external database but neither its settings nor its record say where that database is — or they
  point at the built-in server it does not run — the deployment is rejected with a message naming
  what to set, instead of starting a gate that can never pass.

## What you need to do

Nothing for a running instance. The fix reaches **new provisions** through the next hosting-operator
image: the fleet's operator must run an image built after this change before an instance that hit
this is provisioned again.
