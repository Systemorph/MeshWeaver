---
Name: Course videos now ship with the course
Category: Feature
Description: Installing or updating a course or plugin now publishes the videos, posters and images it commits, so a lesson's video plays straight after the install instead of after someone uploads it by hand.
Icon: Sparkle
Order: -20260808
---

# Course videos now ship with the course

A course was published in two halves. Merging it published the **nodes** — the
lessons, quizzes and exercises — and every portal picked those up on its next
sync. The **videos did not travel that way**. They had to be uploaded to each
portal separately, by hand, with a script.

The two halves drifted, in both directions. A course could be fully merged and
still show a broken video, because nobody had run the upload yet. And a video
could be playing on one portal while existing nowhere in the repository — so a
storage reset would have lost it for good, with no copy to restore from.

Now a course or plugin publishes **completely**. The videos, posters and images
it commits alongside its lessons are installed with it and are playing as soon
as the install finishes. Updating a course brings across whatever media actually
changed and leaves the rest alone, so re-cutting one video does not re-transfer
the whole course.

Anything uploaded directly to a portal stays put: an install adds what the
course ships, and never removes files the course does not carry.
