---
Name: Your installation updates to the latest build, not the highest number
Category: Fix
Description: An installation that keeps itself up to date was choosing its next version by comparing version numbers. When a version number was published by mistake and later withdrawn, that mistake outranked every genuinely newer build — permanently — and the installation could not move again on its own. It now follows the order the builds were actually published in, and it notices when the version it runs has disappeared.
Icon: ArrowSyncCheckmark
Order: -20260907
---

# Your installation updates to the latest build, not the highest number

An installation that keeps itself up to date has to answer one question on every check: *of
everything published, which one should I be running?* Until now it answered by comparing version
numbers, highest wins. That sounds obviously right, and it worked for a long time — until the day a
version number was wrong.

## What happened

On 5 September a version number was raised by mistake and reverted a few minutes later. Ten builds
had already gone out under the raised number. They were removed from circulation two days later.

In between, two installations checked for updates and found those ten. The number was higher, so
they took it — and moved themselves *backwards*, onto builds three days older than what they had
been running, missing work that had shipped since.

Then they got stuck, and this is the part worth explaining. Once an installation is on the
highest-numbered build in existence, nothing can ever be higher. Every later check found the same
answer: *nothing newer than what you are running.* That is exactly the sentence a perfectly
up-to-date installation prints, so from the outside nothing looked wrong at all. Newer builds were
published throughout. None of them could win. Both installations had to be moved by hand.

Removing the mistaken builds did not help either. The next-highest number belonged to a different
older build, for a different reason, and the installations went for that one instead. Sorting by the
number simply found the next wrong answer.

## What changed

**Two things, and the second is the one that matters most.**

**Updates now follow publication order.** Every build carries the sequence number of the run that
published it, and that number only ever goes up — it is produced by the machine that did the
publishing, not typed by a person. That is now what "newer" means. A build labelled with a higher
version but published *earlier* loses, which is precisely the case that caused this. The version
number is still what you see and still what an official release is named by; it is simply no longer
the thing that decides which build is more recent.

**An installation now notices when the version it runs no longer exists.** *"Nothing newer"* and
*"the build I am running has been withdrawn"* used to print the same sentence, and they are opposite
situations: the first is healthy, the second means the installation cannot start a fresh instance of
itself at all and can never be rescued by a future release. The update check already had the
information needed to tell them apart — it just was not asking. Now it does, and:

- The Updates screen says so plainly, at the top, before anything else.
- The installation moves itself to the best build that actually exists, even if that means going
  back a step. A build that exists beats one that does not.
- When there is genuinely nothing to move to, it says that too — and says what an administrator has
  to do — instead of reporting that everything is fine.

There is a deliberate third answer as well. If the check cannot *reach* the list of published builds,
it says the question was not answered, rather than treating a failed lookup as "your version is
gone". Acting on that mistake would send every installation backwards at once.

## If you were affected

If an installation reported *"no newer version detected"* while sitting on an old build, it was not
up to date — it was stuck, and there was no way to tell from the screen. It will now correct itself
on its next check, with no action from you.
