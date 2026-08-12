---
Name: The log store is no longer the first casualty
Category: Fix
Description: Logs now survive the incidents you need them for — the log store gets its own node, durable storage, and an alert when the pipeline breaks.
Icon: Sparkle
Order: -20260812
---

# The log store is no longer the first casualty

When a portal used a lot of memory, the component that stores the logs was the first thing the cluster
shut down to reclaim it — and it stored those logs on a disk that is erased when it moves. So the one
period anybody wanted to look at afterwards was the exact period that no longer existed. The system
that files tickets for errors reads from that same store, so it also reported nothing for the whole
window, which made the outage look like quiet.

Three things changed. The log store now runs on its own machines, separate from the portals it
watches, so a busy portal cannot displace it. It keeps its logs on durable storage that survives being
moved. And there are now alerts on the log pipeline itself, so "we stopped collecting logs" is
something you are told immediately rather than something you discover later.

The error-ticketing system also no longer treats an unexpectedly empty answer as good news: if it asks
for a long stretch of history and gets nothing back, it files that as an incident of its own.
