---
Name: A rule shared between repos can no longer drift apart unnoticed
Category: Feature
Description: The rule blocks that several repositories carry word-for-word are now compared by CI across the whole fleet, so a repo that is missed in a rollout — or edited afterwards — is named in a failing check instead of quietly running under different instructions.
Icon: Sparkle
Order: -20260830
---

# A rule shared between repos can no longer drift apart unnoticed

Each repository in the fleet keeps its working agreements in an `AGENTS.md` that is loaded into
every assistant session in that repository. Several of those rules are deliberately the same
everywhere, and until now the only thing keeping them the same was someone remembering to carry
each edit to all seven repositories by hand.

That worked until it didn't. Nothing compared the copies, so a repository missed in a rollout, or
edited a week later, looked exactly like one that was up to date — and every session in it quietly
followed a different rule.

Shared rules are now marked as such and compared by CI across every repository that carries them.
A copy that goes missing, loses its markers, or differs by so much as a word turns the check red
and names the repository and the exact difference. Spans that are genuinely local — where a
repository keeps its own documentation, which modules it ships — are declared as such centrally, so
they stay free to differ while everything around them is held identical. The comparison also runs
on a daily schedule, because a rule can drift on a day when nobody opens a pull request.
