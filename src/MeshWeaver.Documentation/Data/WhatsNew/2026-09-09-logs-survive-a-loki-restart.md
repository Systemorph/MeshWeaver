---
Name: Logs survive a Loki restart
Category: Fix
Description: A drain of the log store took every namespace's history with it — a query that returned the two portal shutdowns at 01:08Z returned nothing at 01:11Z. The ingester now keeps a write-ahead log and flushes on shutdown, the chart version is pinned, and a gate on every pull request renders these settings and hands the result to the Loki binary itself.
Icon: ShieldCheckmark
Order: -20260909
---

In the night of 2026-09-09, while an incident was being read out of the logs, the log store was
itself drained with its node. A query over `{namespace="memex"}` at 01:08Z returned both portal
pods' `Application is shutting down` lines from 01:05:01Z. The same query, over the same window,
at 01:11Z returned nothing — and the pods no longer appeared in the per-pod counts either. Every
namespace's history now started at the moment of the restart.

## What was missing

Loki already had a persistent volume, and it was working: it preserves chunks that have been
**flushed**, plus the index. Whatever the ingester is still holding in memory is a separate matter,
and covered by a separate setting — the ingester write-ahead log, together with a flush on
shutdown, which turns a graceful drain into a flush instead of a loss. Neither was enabled.

## What it does now

- The ingester keeps a write-ahead log on the persistent volume and flushes what it holds when it
  is asked to stop.
- The chart version is pinned. It was not: every install pulled whatever had been published most
  recently, so an upstream change could have quietly stopped applying these settings.
- A gate on every pull request renders the values against the pinned chart, checks that each
  setting arrives unaltered, and hands the result to the Loki binary to validate.

## Why the gate has two halves

The obvious check — render the chart, assert the settings are in it — is the right one for
Promtail, whose chart silently discards anything it does not recognise. It is useless for Loki,
whose chart copies the whole configuration section through untouched: a misspelled setting arrives
intact and passes. Only Loki itself knows the difference, and it says so plainly
(`field ingestor not found`). The first version of the gate had only the render half and passed a
deliberately misspelled setting — it could not fail for the component it existed to guard.

A drain of the log store is still a read-it-now window: the write-ahead log narrows the loss, and
Promtail, which this chart cannot give a buffer of its own, is holding lines in memory while Loki
restarts.
