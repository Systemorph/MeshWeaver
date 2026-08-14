---
Name: Your name stays on what you asked for
Category: Fix
Description: Work you start is now recorded — and authorised — as you, not as the platform. An internal identity opened for one step could previously carry on into everything composed after it.
Icon: PersonAccounts
Order: -20260814
---

# Your name stays on what you asked for

Some steps genuinely run as the platform rather than as you: reading content you are entitled to but
cannot see directly, creating the storage a brand-new space lives in. That is by design, and it is
scoped to the step that needs it.

It was not staying scoped. The platform identity, once established for one step, carried on into
everything built on that step's result — so an operation you started could have its **later** writes
recorded as the platform instead of as you. A package install did exactly that: the home it created,
every batch of content it copied and the manifest it finished with were all attributed to the
platform, while the activity entry beside them correctly named the person who asked.

Two things were wrong with that, and the second is the reason it is fixed at the root rather than in
the log:

- **The history read wrong.** "Who created this?" answered *the platform* for things a person asked
  for. Nothing in the affected installs landed anywhere the person could not have written — but the
  record no longer matched what happened.
- **Permission questions were being answered for the wrong identity.** The platform identity is
  allowed everything. Any check that ran after the leak was asking about the platform rather than
  about you, so a step you should have been refused could have quietly succeeded.

An internal identity is now sealed into the step it was opened for. What runs as the platform is
exactly what has to; everything composed afterwards is yours again — audited as you, and checked
against your permissions.
