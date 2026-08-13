---
Name: Long activity logs stay fast
Category: Feature
Description: A long-running import, sync or script no longer slows down as its log grows — older lines are archived and the activity's cost per line is now constant.
Icon: Sparkle
Order: -20260813
---

# What's New — 13 August 2026

## Long activity logs stay fast

Every progress line an activity wrote used to re-save the entire log. The hundredth line therefore cost a hundred times what the first one did, and a long import, repository sync or script slowed down steadily as it ran — one import on a busy deployment spent hundreds of megabytes of processing on nothing but re-saving its own log, enough to make the whole portal sluggish while it worked.

An activity now keeps its most recent messages and archives older ones into log segments beside it. The cost of recording a line is the same on line 5,000 as on line 1: in a measured run, quadrupling the number of messages used to cost 15× the processing and now costs 4×. Short activities are completely unchanged.

Nothing is lost. The activity view shows the recent log with a line telling you how many earlier messages were archived, and the archived segments remain readable in full. Progress indicators, previews and the running-activities strip all still show the true message total.
