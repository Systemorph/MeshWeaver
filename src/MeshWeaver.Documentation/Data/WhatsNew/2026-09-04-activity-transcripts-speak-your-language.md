---
Name: Activity transcripts speak your language
Category: Fix
Description: GitHub sync, compile and import activities recorded every line in English, whatever language you read the portal in. Those transcripts are now written with a catalog key and rendered in your language.
Icon: Bug
Order: -20260904
---

# Activity transcripts speak your language

Open a GitHub sync, a NodeType compile or a Space import and you get a **transcript** — the list of
lines the operation wrote as it ran. Until now every one of those lines was English, for every
viewer, and no setting changed that.

The reason is worth stating, because it is not an oversight anyone could have fixed by translating
harder. A transcript line is written **server-side, at the moment the work happens, with no viewer
in scope** — an import runs as the system at boot, a compile runs on a node hub, a sync runs behind
somebody's click. The line is then read later by several people whose languages differ. Resolving a
language at write time would not have been merely hard; it would have been *wrong*, because it
freezes one reader's language into a record everyone shares.

So the lines are no longer stored as finished sentences. They are stored as a catalog **key** plus
the values that vary — the Space, the commit, the counts — and the language is chosen when the page
is rendered, from the locale of whoever is looking at it. A German viewer now reads:

```
Branch 'main' steht auf a1b2c3d4 — Ihr Space ist aktuell.
a1b2c3d4 committet (12 geschrieben, 3 entfernt).
Die Quellensuche fand 14 Code-Node(s): …
```

Every one of the eight GitHub operations — commit, update, re-import, create branch, open pull
request, check branch, sync issues, merge — is covered, along with the compile pipeline's progress
and failure lines and the import's own progress and content-sync ledger.

**Nothing already recorded has changed.** Every line written before this keeps its stored English
text and renders exactly as it did, as does any line whose text was never the platform's to
translate in the first place: a Roslyn diagnostic, GitHub's own refusal message, an exception. Those
stay verbatim on purpose — a compiler's `CS0246` with its file and span says more to whoever has to
act on it than a translation of the sentence around it would, and the status beside it reads the
same in any language.
