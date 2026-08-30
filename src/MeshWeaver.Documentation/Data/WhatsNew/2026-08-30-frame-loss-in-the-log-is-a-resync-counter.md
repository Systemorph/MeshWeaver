---
Name: Frame-loss lines in the log are a recovery counter
Category: Feature
Description: The data-sync page now documents the frame chain that detects a lost update, and states plainly how to read its log line — every entry is a gap that was already repaired, so the count alone never diagnoses anything.
Icon: ArrowSync
Order: -20260830
---

# Frame-loss lines in the log are a recovery counter

Synchronized data travels as a chain: every update the server sends names the update it sent
before it, so a viewer that misses one notices immediately and asks for a fresh copy. That
recovery has been in place for a while, and it works — but it writes a line to the log each time,
and a large number of those lines has repeatedly been read as a large amount of lost data.

It is the opposite. Each line is a gap that was *detected and already repaired*. The data-sync
architecture page now says so, and gives the two numbers that actually diagnose something: how
often a **single** stream logs it, and whether a fresh copy ever follows. It also lists the log
lines to read alongside it, because the cause is almost always something further upstream ending
and re-establishing a subscription — not the chain itself.
