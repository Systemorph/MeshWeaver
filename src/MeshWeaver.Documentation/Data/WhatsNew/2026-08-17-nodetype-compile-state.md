---
Name: Sample data types compile again
Category: Fix
Description: Sample NodeTypes no longer arrive stuck on another machine's old compile error.
Icon: Sparkle
Order: -20260817
---

# Sample data types compile again

Several sample data types — Northwind products and analytics, and the FutuRe business-unit types — showed a compilation error on pages that use them, and clicking around never cleared it. The error text came from a different machine, months earlier: the sample files had been produced by exporting a running portal, so they carried that portal's compile record along with the actual definition. Every portal that loaded them adopted that verdict instead of compiling for itself, and there was no way back — not a restart, not an upgrade.

The sample files now ship only what a person authored: the type definition and its display details. Each portal compiles them itself, so what you see is your portal's real result. A check now keeps exported state from creeping back in.
