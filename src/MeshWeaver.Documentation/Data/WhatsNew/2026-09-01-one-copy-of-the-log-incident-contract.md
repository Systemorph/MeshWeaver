---
Name: One copy of the log-incident contract, not two
Category: Fix
Description: MeshWeaver.Observability.Contract existed in both repos, byte-identical, and both fed the same portal image under one assembly name — a duplicate whose winner was decided by copy-local ordering. The platform's copy is gone, and an embedding project nothing referenced went with it.
Icon: Bug
Order: -20260901
---

# One copy of the log-incident contract, not two

`MeshWeaver.Observability.Contract` is the wire contract for red-log triage: the parser that decides
when two errors are *the same* error, the burst aggregator that turns ten thousand red lines into
one ticketable fact, and the `ILogIncidentIngest` seam the portal resolves to file the incident.

It existed **twice** — once in the platform repo, once in `MeshWeaver.Plugins` — with all twelve
source files byte-identical. And both copies reached the *same portal image*: the endpoint that
files incidents referenced the plugins copy, while the shared portal library referenced the
platform's, and the first references the second. Two projects, one assembly name, one publish.

Which copy won was decided by the copy-local step. The file that describes the hazard was sitting
inside the duplicate, saying so:

> *"the portal image builds BOTH — so the predicate has to exist in each, or the type is missing at
> runtime whenever the other copy wins the copy-local step."*

The tests had duplicated too, and had started to drift apart: the same fifteen test names in both
repos, 56 lines different, with the plugins copy quietly the stronger one — it exercised five real
pipeline findings where the platform's exercised one, and checked the incident fingerprint against
the report a live watcher actually produces instead of against a hard-coded string.

**Now there is one copy**, in the repo that owns the watcher and the module that implements the
seam. The platform's copy, its ships-the-bits reference, and its weaker duplicate test are deleted.
The two observability suites that had no platform dependency at all moved across whole.

One test spanned the boundary and was **split rather than moved**. It pinned that a compile failure
stays diagnosable after travelling the log pipeline — half a property of the compiler's report
format, half a property of burst aggregation. Moving it whole would have taken the compiler's only
coverage out of the compiler's own CI. The ordering half stayed; the end-to-end half moved; each
now names the other.

Travelling with it: **`MeshWeaver.Hosting.Embeddings`**, the embedding-provider seam behind vector
search. It was in the solution, built by CI and published to nuget.org — and **nothing in the
platform repo referenced it.** Not one project file. Its only three consumers were already in the
plugins repo; it had simply failed to travel with the storage backends it belongs to. A project with
no consumers keeps every signal green, which is exactly why it sat there.

The map of what is left, what pins it, and how to finish a move without leaving a second copy behind
is now written down in **Doc/Architecture/CarvingProjectsOutOfCore**.
