---
Name: Progressive Queries
Category: Architecture
Description: "MeshQueryRequest.Progressive lets a caller that keeps consuming a query stream receive each partition's rows as that partition answers, instead of one Initial after the slowest partition. Opt-in, because many callers take the first Initial as the whole answer."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 12h4"/><path d="M10 12h4"/><path d="M16 12h4"/></svg>
---

# Progressive queries

## The one sentence

**A request with `Progressive = true` may be answered in pieces: a provider that reads several
partitions emits its `Initial` as soon as it has rows, and delivers the rows of the partitions that
answer later as `Added` changes. Without the flag nothing changes.**

## Why it exists

A query that names no partition reads every partition the caller can read. The PostgreSQL provider
used to answer it with ONE statement over all of them, so the first row reached the caller only after
the slowest partition had been read. On the public instance (2026-11-21) the search bar's free-text
query read 173 partitions and its first row arrived after a median of 7 s, although most single
partitions answer in tens of milliseconds (MeshWeaver.Plugins `Hosting/SearchSuggestionLatency`).

## The contract

- **Opt-in, per request.** `MeshQueryRequest.Progressive` defaults to `false`. Many callers take the
  first `Initial` as the complete answer — `.Where(c => c.ChangeType == Initial).Take(1)` — and under a
  progressive answer that `Initial` is a subset. Only a caller that keeps consuming the stream and
  treats every frame as "the answer so far" may set it.
- **Same rows in the end, same rules throughout.** A progressive answer reads the same partitions
  under the same access rules as the non-progressive one, and once every partition has answered its
  result IS the non-progressive answer. Under a limit an early row may be withdrawn on the way there:
  a `Removed` change, when a better-ranked row from a later partition displaces it from the top
  `limit`.
- **Never an early empty frame.** A provider emits a progressive `Initial` only once it has a row, or
  once every partition has answered. So an empty frame still means "the answer is empty" and never
  "not yet" (MeshWeaver#6827 — the search dropdown closed on an empty seed).
- **A provider may ignore it.** A provider that does not honour the flag answers as it always did.

## What the fan-in does with it

Nothing special, and that is the point. `MeshQuery.MergeProviderObservables` already gates its merged
`Initial` on every provider's `Initial` and forwards later `Added` changes unchanged, and the snapshot
surface (`IMeshService.Query`, `CombineAnswers`) already emits a frame per provider answer. A
progressive provider's early `Initial` therefore reaches the caller at once, and its `Added` changes
follow as further frames. `ProgressiveQueryRequestTest` pins this on both surfaces, and pins that a
request without the flag still gets one complete `Initial`.

## Who honours it

The PostgreSQL partitioned provider in MeshWeaver.Plugins (`PostgreSqlPartitionedMeshQuery`) for an
unpinned fan-out: one statement per partition, a bounded number at a time, each through the I/O pool.
Its first caller is the portal search bar (`MeshSearch` in MeshWeaver.Plugins).
