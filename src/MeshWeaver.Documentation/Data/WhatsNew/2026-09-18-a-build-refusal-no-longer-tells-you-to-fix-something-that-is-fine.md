---
Name: A build refusal no longer sends you to fix something that is fine
Category: Fix
Description: When a build could not read which platform release the repository's main branch had last passed, it closed by telling you to fix main — even when main was perfectly healthy and GitHub had simply served a month-old page of build history for that one call. The refusal now shows what it read and leads with the check that tells the two apart.
Icon: Bug
Order: -20260918
---

# A build refusal no longer sends you to fix something that is fine

Every pull request in a package repository builds against the newest platform release **that its own
`main` branch has already passed on**. That is deliberate: if a platform release breaks the
repository, `main` goes red on it alone instead of every open pull request going red at once.

To apply the rule, the build reads the repository's recent successful `main` builds and looks up
which release each of them used. If it cannot find one, it **refuses** — it never guesses a release.
That refusal is correct and is unchanged here.

## What was wrong with the message

There are **two** reasons that read can come up empty, and they need opposite responses:

- `main` really has not passed anything recently — then `main` needs attention;
- GitHub served *this one call* a stale page of build history — then nothing is wrong at all, and
  the build simply needs to run again.

The message named both, but it closed on the first one:

> …**Fix main**, or set the repo VARIABLE MW_PLATFORM_REF to select one set explicitly.

On 2026-09-17 that sentence was read three times. Twice, someone went and looked at a `main` that
was healthy; once, it produced the conclusion that *no pull request in the repository could build at
all* — while four of them were building successfully in the same minutes. The build history GitHub
had served for that one call was **five weeks old**, and the same build succeeded on a re-run about
twenty minutes later with nothing changed. It was the third time this had happened.

## What changes

The refusal now prints **what it actually read** — how many builds it examined, and the newest one's
number, date, age and link — and then gives remedies in an order the evidence decides:

1. **Check the history first.** Open the repository's successful `main` builds. If any is newer than
   the date printed, the page was stale, `main` is fine, and re-running the build is the whole fix.
2. **Only if that really is the newest one**, `main` has passed nothing — then fix `main`.
3. Either way, a specific release can still be pinned to get moving.

Where the evidence points the other way — recent builds that *did* report a release and still named
none — the message leads with `main` instead, and says which builds implicate it.

Nothing about the refusal got weaker: it still refuses rather than guessing a platform release, it
still does not retry by itself, and it still never falls back to the newest release.

## Why this is worth a note

A message that names the wrong cause is worse than one that says nothing, because it spends the
reader's time in the wrong place and they trust it while doing so. The explanation was already in
the old text — one sentence, correct, sitting behind twelve lines of detail and ahead of an
instruction that said something else. **Position is part of the message.**

The full account — what was measured each of the three times, what the two build-history reads can
and cannot check, and why neither of them retries — is
[A Stale Run Listing Is Not a Broken Main](/Doc/Architecture/StaleRunListingRefusals).
