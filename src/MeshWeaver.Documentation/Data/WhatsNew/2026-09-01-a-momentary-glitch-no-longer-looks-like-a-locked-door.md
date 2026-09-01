---
Name: A momentary glitch no longer looks like a locked door
Category: Fix
Description: When the platform briefly could not work out whether a page is public, it used to send visitors to the sign-in screen as though the page were private. It now says the page is temporarily unavailable — and says so in the log, where nothing was written before.
Icon: LockOpen
Order: -20260901
---

# A momentary glitch no longer looks like a locked door

Every page a logged-out visitor opens is checked against one question: *is this page published to
the public?* Most of the time the answer is a clear yes or a clear no. Occasionally the check itself
cannot complete — a moment of load, a component restarting in the background, a slow reply that
never arrives.

Until now those three situations produced the same answer. "We could not work it out" was recorded
as "no", so the visitor landed on the sign-in screen for a page that may well have been public, and
would keep landing there for as long as the hiccup lasted. Nothing was written to the log, so from
the outside a passing glitch was indistinguishable from a page that is genuinely private — the
symptom reached us as support questions rather than as anything anyone could search for.

The check now has three answers rather than two:

- **published** — the page loads, exactly as before;
- **not published** — sign-in first, exactly as before;
- **could not be determined** — nothing is served, nothing is claimed about the visitor, and the
  answer is *temporarily unavailable* rather than a sign-in bounce. It is also written to the log,
  naming the page and the reason, so a recurring one is now something an operator can find.

Nothing becomes more visible: a page whose check does not complete still shows nobody anything. The
change is only in what the visitor and the log are *told* — an honest "try again in a moment"
instead of a confident, wrong "this is not for you".

Public pages, published catalogs and course covers are unaffected, and a page that really is private
still asks for sign-in in exactly the way it did before.

**The same mistake, in three more places.** Reviewing the fix turned up other actions that gave the
same confident wrong answer when their check hiccuped:

- **Recycling a node** replied *"Recycle requires Update permission — ask someone with write
  access"*. Told to someone who already had write access, that sends them to interrupt a colleague
  over nothing. It now says the check did not complete and to try again.
- **Exporting** quietly left a node out of the ZIP. It is still left out — that is the safe
  direction — but the omission is now recorded, so an export that is short a node is something you
  can find out about rather than something you discover later.
- **Setting up a new personal or shared space** skipped a repair step without distinguishing "not
  needed" from "could not tell". The visible behaviour is unchanged; the difference is now in the
  record.
