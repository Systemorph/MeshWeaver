---
Name: A type no longer reports "Ok" when its pages cannot open
Category: Fix
Description: A custom type could report a clean, successful build while every one of its pages was dead — because "the build succeeded" and "this portal can open it" are two different questions and only the first was being asked. The portal now refuses code built for a different platform version, says so plainly, and rebuilds it.
Icon: ShieldError
Order: -20260907
---

Your custom types are compiled by the portal, and the result is recorded on the type: *the build
succeeded, here is the code it produced.* What that record never said is **which platform version
the code was built for** — well, it did, in a separate field that nothing read alongside the
verdict.

So a type could sit there reporting a clean, successful build while every page of every record of
that type was dead. On 2026-09-06 exactly that happened to two types on a client portal: their code
had been built for a different platform version than the one actually serving, so no page could
open — and the type's own status, the deploy checks, the diagnostics and the "compiling…" page all
said it was fine. Every deal page and every offer page was down for two and a half hours behind a
green tick.

**"The build succeeded" and "this portal can open it" are different questions**, and the second one
can only ever be answered by the portal asking it. Both are now answered:

- **Code built for a different platform version is refused, not loaded.** Loading it would fail
  deep inside the runtime in a way that produces no error at all — the page simply comes up empty,
  permanently, for as long as that page's session lives. Every place the portal picks up compiled
  code now checks first.
- **Refusing does not mean the type is broken.** It falls back to compiling the type here, against
  the version that is actually running — which is what already happens after any ordinary platform
  update.
- **The status stops claiming to be Ok.** A type in this state now reports a distinct state of its
  own, naming both platform versions, in the diagnostics and on the type's page. It is deliberately
  not reported as a compile *error*: nothing is wrong with your code, and telling you to fix it
  would send you looking for a bug that is not there.
- **The "compiling…" page stops bouncing you.** It used to read the green status and redirect you
  straight back to the page that could not open, which sent you back to it. It now stays put and
  explains, in your language.
- **And the record itself was being written wrong.** In one case the portal loaded perfectly good
  code and then wrote down someone else's platform version next to it, which pinned the type in
  that state with nothing able to get it out. The version a record names now always comes from the
  same place the code does.

Nothing changes for a type whose code was built by the portal that is serving it, which is every
type on a portal running one version.

The full design — including why this needed a new state rather than a redefinition of "Ok", and why
it is worked out fresh by each portal instead of being written onto the shared record — is in
[Build Identity Admission](/Doc/Architecture/BuildIdentityAdmission), alongside its companion
[Mesh Admission](/Doc/Architecture/MeshAdmission).
