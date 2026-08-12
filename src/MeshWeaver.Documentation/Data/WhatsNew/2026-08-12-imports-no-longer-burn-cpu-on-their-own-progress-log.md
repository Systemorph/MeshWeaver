---
Name: Imports no longer burn CPU on their own progress log
Category: Fix
Description: Large repository imports are dramatically cheaper, so the portal stays responsive while one is running.
Icon: Sparkle
Order: -20260812
---

# Imports no longer burn CPU on their own progress log

While a large import was running, the portal could slow to a crawl — pages that took seconds to
appear, chats that stalled mid-answer — and it got worse the bigger the import was.

The cause was the import's own progress log. Every file it kept, pruned or failed to write was
recorded as a separate update to the import's activity record, and each of those updates re-read and
re-compared the entire record. So the hundredth line cost a hundred times what the first one did, and
a single import on one portal ended up rewriting hundreds of megabytes just to describe what it had
done. That work was competing with everything else the portal had to do.

The import now records each stage once, with all of that stage's lines together. The activity log
still names every file it kept and every node it pruned — nothing is lost from the audit trail — but
the cost no longer grows with the size of the import. The one visible difference is that progress now
appears stage by stage rather than file by file.
