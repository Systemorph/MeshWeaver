---
Name: A cell that ran no longer claims it never did
Category: Fix
Description: When the bookkeeping write that records a script run failed, the cell reported itself as never run after the next page load — even though the run had happened and its log was still there. The cell now finds the run itself, and says its output is unverified rather than pretending there is none.
Icon: History
Order: -20260905
---

# A cell that ran no longer claims it never did

Press **Run** on a code cell and two separate things are written. The run itself gets its own
record — the activity, with its log, its status and everything the script printed. Separately, a
short note is written back onto the cell: *this cell was run, at this time, by this person, and
here is where the log lives*.

That second note is what the cell reads on your next visit. It is one small write, and very
occasionally it does not land — a busy moment at startup, a refused write, content the platform
cannot read back. When that happened, the note was simply absent, and the cell had nothing left to
tell you with.

So it told you the only thing it could see: **nothing had ever run here.**

That was wrong, and it was wrong in the least helpful direction. The run had happened. Its log was
still sitting in your activity list, complete. The only thing missing was the cell's pointer to it —
and the cell reported that missing pointer as a missing run.

## What happens now

The cell looks for its own runs instead of relying on the note. Every run writes down which cell it
came from, before it starts, so the trail back exists whether or not the note was ever written.

- A cell whose note is intact behaves exactly as before — nothing extra happens, and nothing got
  slower.
- A cell whose note went missing now finds the run and says so, marking its output **unverified**:
  the run is real, but nothing recorded which version of the code it ran, so the cell will not
  claim the output matches what you are looking at.
- A cell nobody has ever run still says nothing at all. An unrun cell has no output to be wrong
  about, and it should not carry a warning.

"Unverified" rather than "up to date" is deliberate. The recovered run proves *that* the cell ran;
it does not prove *what* it ran. Telling you the output is current when nothing substantiates that
would be a wrong claim, not merely a missing one — so the cell tells you what it actually knows and
lets you decide whether to run it again.
