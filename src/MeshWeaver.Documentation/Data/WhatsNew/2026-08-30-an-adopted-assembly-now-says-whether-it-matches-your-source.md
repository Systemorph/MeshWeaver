---
Name: An adopted assembly now says whether it matches your source
Category: Fix
Description: A NodeType that took a prebuilt assembly instead of compiling looked identical to one that compiled — same Ok status, same "sources up to date" — even when the bytes were built from older source. Adopted builds now carry their provenance on the record, and an adoption that provably disagrees with your source is refused instead of accepted.
Icon: ShieldError
Order: -20260830
---

# An adopted assembly now says whether it matches your source

When a NodeType can take a ready-built assembly from a package instead of compiling it here, it
does — that is what makes installs and restarts fast. The problem was that afterwards **nothing
told you which had happened**, and the two states were indistinguishable by every signal you would
think to check: the type read **Ok**, and its sources read **up to date**.

They read up to date because the adoption said so. Taking a prebuilt assembly *also* recorded
"these are the sources it was built from" — copied from whatever the type was holding at that
moment, without ever comparing them to the bytes. So the staleness check was not broken; it was
answering a question the adoption had already answered for it.

On 30 August that cost a client real data: a sync pulled new source, took a prebuilt assembly built
from **older** source, and reported success. The next run executed the old code and stripped the
text out of four documents — one of them a 7,000-word offer, and one that could not be recovered.
Every check available said the fix was live.

## What changed

A package can now record **which sources an assembly was built from**, and the check happens where
it can actually be made — on the NodeType itself, against the sources it is holding right now:

| | what happens |
|---|---|
| the package records sources and they **match** | adopted, marked **verified** |
| the package records sources and they **disagree** | 🚨 **refused** — the assembly is not accepted, and your source is compiled instead |
| the package records nothing (anything published so far) | adopted, marked **unverified** |

Every NodeType now carries this as **build provenance** you can read on the type — compiled here,
adopted and verified, adopted but unverified, or an adoption that was refused. That is the piece
that was missing: not a better checker, but a record of whether anything was ever checked.

**"Unverified" is not a warning that something is wrong.** It means nobody has compared these bytes
to your source, because the package that supplied them does not yet say what it was built from.
Until packages start recording that, every adopted assembly reads unverified — which is simply the
truth, and better than a record that claims a check that never happened.

## What still needs care

- A refused adoption **stops serving** the rejected assembly and compiles your source instead — on
  an installation that is allowed to compile. On one configured to use only prebuilt assemblies
  there is no local compile to fall back on, so the assembly is left in place (nothing at all would
  be worse) and a **critical** message names the type: that one needs the package rebuilt and
  republished.
- Something that is *armed to run* against a NodeType — a scheduled or triggered action — does not
  yet refuse to run against an unverified one. That interlock is the next step and is tracked
  separately; provenance is what makes it possible to state.
