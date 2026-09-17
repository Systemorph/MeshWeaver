---
Name: A pull request now shows when an automatic review finding is still unanswered
Category: Feature
Description: A new check on every pull request into main stays red until the automatic code review has actually reviewed it and every finding that review raised has a reply — so a change can no longer slip in with its review unread. It is being observed before it becomes a merge requirement.
Icon: ShieldCheckmark
Order: -20260917
---

# A pull request now shows when an automatic review finding is still unanswered

Every change to the platform is reviewed automatically before it can merge. Until now, nothing
checked whether anyone had *read* that review: a change whose tests finished quickly could merge
while the review's findings sat unanswered. One such change, with fourteen unread findings, stopped
new plugin releases from being published for six hours.

A new check, **Automatic review answered**, now appears on every pull request. It stays red until:

- the automatic review has really reviewed the change — a message saying the reviewer *could not*
  review it (for example because its monthly allowance ran out) does not count, and
- every finding the review raised has a reply from a person, whether "fixed" or an explanation of
  why not.

If the reviewer genuinely cannot review a change, only a maintainer can release it, and the release
is visible on the pull request.

For now the check is shown but does not block a merge. It becomes a requirement once it has been
watched on real pull requests.
