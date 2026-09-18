---
Name: Two messages that broke off mid-sentence now finish
Category: Fix
Description: One message about a package running the copy from the installation's image ran two phrasings together and came out ungrammatical; another about when a workspace will move forward ended on the word "is" and never said what it was waiting for. Both now read as sentences, and neither changed meaning.
Icon: Bug
Order: -20260918
---

# Two messages that broke off mid-sentence now finish

Two explanations you can meet in the app were unfinished sentences. Nothing behind them changed, and
neither was ever wrong about *what* was happening — they just failed to say it.

## The package that is running the image's copy

When a package you installed cannot run because it was built against a different platform build, the
app explains that the copy shipped in the installation's own image is running instead. The last
sentence said:

> …it starts running once it is **published built against** this platform.

Two ways of putting it had run together. It now reads:

> …it starts running once **a build made against this platform is published**.

Same condition, said once.

## The workspace waiting to move forward

When a workspace's sources are held back to match the exact software the installation runs, the app
lists the two things that would let it move forward. The second one ended on the word *is*:

> …or when this instance is rolled onto a platform whose publication **is**.

The missing word is **sealed** — the sentence's own first half says it (*"when a newer commit … **is
sealed** for framework identity …"*), and the German translation of this very message has said
*"deren Publikation **versiegelt ist**"* all along. So nothing had to be guessed at; the word was
simply dropped in English. It now reads:

> …or when this instance is rolled onto a platform whose publication **is sealed**.

## What this means for you

**Nothing to do, and nothing behaves differently.** These are the words on screen. If you read either
message before and came away unsure what you were waiting for, the sentence — not the situation —
was what let you down.

German was already correct for both and is unchanged.
