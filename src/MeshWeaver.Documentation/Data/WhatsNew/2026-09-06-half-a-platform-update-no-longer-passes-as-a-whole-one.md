---
Name: Half a platform update no longer passes as a whole one
Category: Fix
Description: A content repository names the platform build it works against in several places at once, and three repositories had nothing checking that those places agreed — so an update that changed some of them and missed the rest passed every check. They now agree, or the repository says so.
Icon: Link
Order: -20260906
---

# Half a platform update no longer passes as a whole one

Each content repository is tied to one specific build of the platform, so that two runs of the same
content give the same answer. That build is not named once. It is named several times over — the
version the checks run inside, the version the content is compiled against, the version of the
shared build steps themselves — because the places that need it cannot read one another.

Nothing was holding those copies together. Change some of them and miss the rest and every check
still passed, while the repository was quietly working against two different platform builds at
once: the checks judging one, the content built against another. The consequence does not appear
where the mistake was made. It appears days later, somewhere else, as a mismatch that names none of
the settings involved. It has cost an hour of stopped delivery before, with a green tick on
everything.

Two of the six content repositories already had a check for this. Three had nothing at all — not a
check that was being skipped, which at least leaves a trace, but no check to skip. Those three now
have one, and it runs on every change before it can be merged.

It compares what a repository says about the platform in each place it says it, and refuses three
things that used to pass: two settings for the same image disagreeing, a shared build step pointing
at one version of the platform while asking it to run another version's code, and a setting whose
value has been replaced by a placeholder — which the previous generation of these checks read as
"nothing is set here" and let through. Once a day it goes further and asks the registry directly
whether the images a repository names really do come from one build, which is the only way to relate
two of them.

It also reports what it examined, not only what it found. A repository whose settings the check
cannot locate is reported as unchecked rather than clean — the two had looked identical, and telling
them apart is most of the value.

Building it turned up the same blind spot in the check that shipped alongside it a day earlier:
tested against the real repositories rather than against an invented example, that one also let a
placeholder through, in the one place two of the six repositories happen to write it. Both are fixed
and both now prove they can fail before they are allowed to pass.
