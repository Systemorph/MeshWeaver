---
Name: Five more search fields now mean the same thing on both backends
Category: Fix
Description: Who created a node, when, who last changed it, its desired id and its sync behaviour were read from the node itself when a search ran in memory and from an empty content field when the same search ran against the database. The two backends now agree, and the list of fields they still disagree about is half what it was.
Icon: Search
Order: -20260907
---

# Five more search fields now mean the same thing on both backends

The query language has two implementations — one that runs in memory and one that runs against the
database — and a shared list records, field by field, where each of them reads a value from. When
the two rows differ, the same search returns different results depending on which backend answered.

Ten fields were on that disagreement list. Five of them have now been closed: **who created a
node**, **when it was created**, **who last changed it**, its **desired id**, and its **sync
behaviour**. The in-memory backend always read these from the node itself; the database backend
looked inside the node's content, where almost nothing keeps a copy, and answered "nothing
matches" — while the value sat right there on the node being returned.

## What changes here

Only the shared record. The database-side fix lives with that backend, and it is more than a longer
list of names: those five columns exist on the main table and **not** on the tables that hold
threads, activities, access records and comments, so the same field has to resolve differently
depending on what is being searched. The record now says so per field, and each backend's own tests
hold it to exactly that.

## What is still on the list, and why

Five names remain. Four of them — whether a node is definition-only, whether it is a satellite
type, its pre-rendered HTML, and whether it names a main node explicitly — exist only on the
in-memory representation. There is no column behind them, so no amount of widening reaches them.

The fifth is the interesting one: the per-node **"hide this from search / from the header / from
create"** list. It does have a real column, but it holds several values at once rather than one, so
asking about it means asking "is this one of them" — a different question from the "is it equal to
this" every other filter asks. It stays on the list on purpose, with that reason recorded next to
it, rather than being closed in a way that would break the query outright.
