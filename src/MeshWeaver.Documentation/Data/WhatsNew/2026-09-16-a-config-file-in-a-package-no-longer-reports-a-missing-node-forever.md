---
Name: A config file in a package no longer reports a missing node forever
Category: Fix
Description: An ordinary tsconfig.json or package.json inside a package folder was counted as a node the install owed the mesh, so every boot of every pod reported it ABSENT at Error — and no reinstall could ever clear it. The installer now records which files it could not read as nodes, and the completeness sweep reads that instead of guessing.
Icon: Bug
Order: -20260916
---

# A config file in a package no longer reports a missing node forever

The install-completeness sweep compares what a package's install record declares against what is
actually in the mesh, and names anything missing. It is the instrument that catches a node lost
after an install — the failure that once went eleven days unnamed and took a portal down with it.

Its value depends entirely on counting the **right population**. Deciding whether a shipped file
becomes a node has two halves, and only one of them can be answered from a path. The first half —
does any registered parser claim this extension — was unified across the two sides earlier: a `.tsx`
view or a `.png` is not a node, and counting it produced a phantom ABSENT that re-fired on every pod
boot forever.

**The second half needs the bytes, and nothing was asking it.** A file whose extension *is* claimed
can still fail to become a node on its content: a well-formed `.json` carrying no `$type`, `id` or
`nodeType` is not a node, which is exactly what every `tsconfig.json`, `package.json` and `app.json`
looks like — and a package that ships a small front-end (a `gui/rn/` folder, say) ships several. The
installer parsed such a file, got nothing, and wrote nothing. The sweep, working from paths, counted
`{Package}/tsconfig` as a node the install owed the mesh and reported it **ABSENT, at Error, on
every boot of every pod**.

Nothing could clear it. The line's own advice — *reinstalling it now repairs it* — is false for this
class: a reinstall meets the same bytes and fails the same way. So an unactionable finding sat at
the same log site, in the same words, as the genuinely lost node the sweep exists to surface.

**The fix is evidence rather than a longer list of exceptions.** Only the installer holds the bytes,
so the installer now records which declared files it could not read as nodes, and the sweep reads
that instead of re-deriving a question it cannot ask. An exclusion list would have needed a new case
for `.tsx`, then for `package.json`, then for whatever ships next — which is how this was reported
twice.

Such a file is still a fault and is still reported: a package that declares a node it can never
deliver gets its own line, at Error, saying what it actually is — a packaging defect, with the
remedy that can work (move the file out of the package folder, or give it a shape the parser
recognises). It simply stops being counted as an absence, and stops driving reinstalls that cannot
help. A genuinely missing node still reads exactly as it did.

Three details that keep it honest. A record stamped before this existed records **nothing**, and
that is kept distinct from "checked, and all of them parsed" — an unknown must never read as a clean
result. An incremental update examines only the files it fetched, so the record merges rather than
replaces, or the next boot would start re-reporting everything outside the delta. And the count
travels on every verdict line, so the day a package does ship such a file it announces itself
instead of arriving as an absence nobody can fix.

The full contract, including the merge rules and what the sweep still does not cover, is in
[Install Completeness](/Doc/Architecture/InstallCompleteness).
