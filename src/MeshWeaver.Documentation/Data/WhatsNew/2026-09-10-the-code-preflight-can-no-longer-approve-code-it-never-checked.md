---
Name: The code assistant's pre-flight can no longer approve code it never looked at
Category: Fix
Description: Before an assistant edits the source of a type, it asks the compiler whether the proposed text is valid. When it could not reach that type at all — a wrong path, a space you have no access to, an area that did not answer — the check reported "no problems found", which is indistinguishable from a clean bill. It now says it could not check, and names why.
Icon: Wrench
Order: -20260910
---

# The code assistant's pre-flight can no longer approve code it never looked at

When an assistant proposes a change to the source code of a type, it does not simply write the file
and hope. It first asks the compiler: *given everything else this type is built from, would this
text compile?* Answers come back as a list of problems, and an empty list means "go ahead".

**An empty list also meant "I could not look."** Those two answers were spelled the same way.

## What went wrong

Asking about a type the check could not reach — a path with a typo in it, a type that had been
renamed, a space the asker has no access to, or an area that simply did not answer in time — came
back as *no problems found*. Not "not found". Not "I could not check". A clean bill of health, from
a check that had never run.

The result was measured deliberately, and it is the clearest possible statement of the problem: the
check was handed text that is not valid code in any language, against a type it could not reach, and
it still answered **no problems found**.

A verification step that cannot report a failure is not a verification step. Here it sat at the
front of the assistant's edit loop — propose, check, fix, repeat — so for any type it could not
reach, every proposal passed on the first try.

## What changes

The check now distinguishes four answers instead of two, and only the first can be read as approval:

- **Checked** — the compiler really ran. An empty problem list here, and only here, means the
  proposed source is clean.
- **Not found** — nothing exists at that path. Renamed, mistyped, deleted, or somewhere this
  replica does not hold.
- **Nothing to compile** — the path is real, but it is not a kind of node that has source code.
- **Could not be checked** — the owning area did not answer, or the read failed. This is the one
  that used to look greenest, and it says nothing at all about the code.

The last three come back explicitly as *not approved*, with a sentence naming the path that could
not be checked, so the assistant reports the real obstacle instead of proceeding on a phantom
approval.

## What is deliberately unchanged

The live squiggles in the portal's code editor keep their silence. There, "I could not resolve the
owner" genuinely should draw nothing: red underlines computed under the wrong assumptions are worse
than no underlines at all. What was wrong was never the silence — it was that a tool rendering a
verdict was reading the same silence as a yes. The editor and the verdict now have separate answers
rather than one shared compromise.
