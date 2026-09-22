---
Name: Guidance that sent authors after a warning they could never see
Category: Fix
Description: The standard that in-mesh code is held to named one warning code where it meant the opposite one — pointing authors at a warning the platform deliberately never reports, and never naming the one that actually holds up a change. One page said both things about the same code, three paragraphs apart.
Icon: Bug
Order: -20260918
---

# Guidance that sent authors after a warning they could never see

Code written **inside** an installation — the small pieces of C# that define a content type, a page,
a script — is held to the same standard as the platform's own source: the compiler's complaints are
collected, compared against a recorded list, and a change that adds a new one is refused. The
refusal names the warning by its code, so the author can go and look it up.

Two of those codes are almost the same words in the opposite order, and the guidance had them
swapped.

## The two codes

A doc comment can describe a function's inputs. There are two different things that can go wrong:

- **A description that names an input which is not there** — the comment describes a parameter the
  function no longer has. The comment *exists* and is *wrong*; anyone reading it is misled. This is
  the breakage that a move between repositories causes, and it counts.
- **An input with no description at all** — the comment simply does not mention it. Nothing is
  wrong; something is merely missing.

The platform treats them completely differently. The first is a real defect and will hold up a
change. The second is documentation debt: it is **deliberately not reported at all**, exactly as the
platform's own source does not report it, so that an undescribed parameter can never be the reason
an unrelated change cannot go in.

## What was wrong

Four places described the *second* code while giving it the *first* code's meaning, and filed it
with the real defects. The costliest was the working agreement every assistant reads at the start of
every session; the others were the standard's own reference page and the comment on the code that
does the sorting.

The effect on a reader was the same each time: told that the change was held up by "a doc comment
that is actively wrong", they would go looking for that code — and find it suppressed, unreportable,
and impossible to have caused anything. Meanwhile the code that genuinely does mean *this comment is
wrong* was never named anywhere they would look.

The reference page had drifted far enough to disagree with itself. Three paragraphs in, it filed the
code with the real defects; a hundred lines later, its own table of what the platform never reports
listed the same code there. One page, two opposite claims, and no way for a reader to tell which
half to believe.

## What changes

The four places now name the code that actually means *this description is wrong*, and say plainly
that the other one is its opposite and can never reach that check. The six places that had it right
all along — every one of them describing it correctly as debt rather than a defect — are unchanged.

Nothing about the checking changed, and nothing was being missed: the sorting was always correct,
because it files everything that is not an undescribed member as a real defect. What was broken was
only the instruction, which is the part a person acts on.

The full account of the standard, both lists, and which codes reach which check is
[The In-Mesh Warning Standard](/Doc/Architecture/InMeshWarningStandard).
