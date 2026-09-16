---
Name: A node type that cannot compile now names the import that lost its file
Category: Fix
Description: When an import cannot write one source file, the files that reference it still land — so the node types built from them fail on "the type could not be found", about a symbol whose file is plainly in the repository. The compile error now says an import refused that specific file, and why, without anyone having to know to go looking.
Icon: DocumentError
Order: -20260916
---

# A node type that cannot compile now names the import that lost its file

When an import cannot write one source file into a Space, the files that *reference* it land
perfectly well. The result is a Space that is complete enough to look fine and incomplete enough not
to build: every node type compiled from those files fails, and the only thing you see is

```
CS0246: The type or namespace name 'SelfUpdateRouting' could not be found
```

— about a symbol whose file is **right there in the repository**. Nothing in that message, and
nothing on the node type, connected it to the import that produced the state.

[The previous fix](/Doc/Architecture/AContentVerdictIsPerNode) made the *sync activity* say which
file did not land and why. That helps if you happen to read the sync activity. Most people arrive
from the other end: something stopped working, and the node type shows a compile error.

## What changes

A node type whose compile fails on an unresolved name now leads its recorded error with the cause:

> **REFUSED BY AN IMPORT:** 1 source node(s) this compile needs were declared by the partition's
> source and could NOT be written to the mesh, so the unresolved name(s) below are missing FILES,
> not missing modules and not a mistake in the code that references them:
> `Hosting/Deployment/Source/SelfUpdateRouting` (the database refused a character it cannot store).
> No framework or module change can supply them — fix the source file in the repository and
> re-import.

You see it wherever you already look at a failing node type: the compile-error page, the diagnostics
tools, and the node's own record. The compile activity carries the same statement as a proper
message, so it reads in your own language. And it survives: once a broken type is parked, every
later visit is served from a cached verdict, and the explanation is re-composed rather than dropped
at exactly the moment someone arrives to read it.

## It only says this when it is true

A missing symbol has three possible causes, and they look identical from the compiler: an import
lost the file, the file was deliberately **deleted**, or the module that carries it **is not loaded
here**. Those have three different fixes, and telling you the wrong one is worse than telling you
nothing — it sends you to the repository looking for a file that was never there.

So the platform says an import refused a file only when it can show both halves: the Space's own
import bookkeeping **records** a refusal for a source file, *and* this compile failed on the name
that file would have declared. A Space with an unrelated refused file, and a node type failing on
some other missing name, is told nothing at all — and "we could not check" is never reported as
"nothing was lost".

Background, with the measurements: [A Parked Type Names the
Import](/Doc/Architecture/AParkedTypeNamesTheImport).
