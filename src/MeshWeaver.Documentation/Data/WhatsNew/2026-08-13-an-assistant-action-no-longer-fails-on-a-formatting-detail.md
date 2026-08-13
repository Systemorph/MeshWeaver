---
Name: An assistant action no longer fails on a formatting detail
Category: Fix
Description: When the assistant tried to create or change a page, the request could fail outright over how it had packaged the details — ending the reply with an error instead of doing the work.
Icon: Sparkle
Order: -20260813
---

# An assistant action no longer fails on a formatting detail

Ask the assistant to write you a story, draft a page, or adjust something you already have, and it
does that by calling one of the platform's own actions — create this, change that. Those actions
take the details of what to write as a small structured document.

Sometimes the assistant packaged that document one perfectly reasonable way and the platform
insisted on the other. The two are the same information written down slightly differently, and the
instructions the assistant is given actually show the very form that was being refused. But the
mismatch was fatal rather than cosmetic: the action never ran, and the whole reply ended in an error
rather than the assistant noticing and trying again. From your side, a request that should simply
have worked came back broken, with nothing useful to act on.

The platform now accepts both forms for these actions and reads the details out of either one. What
happens next is unchanged — the same checks, the same permissions, the same result.

The tolerance is deliberately narrow. It applies only where an action asks for a structured document
in the first place, and only to the two ways of writing one down. Anything else the assistant might
send that genuinely does not fit still fails plainly, so a real mistake stays visible instead of
being quietly reinterpreted into something that was never asked for.
