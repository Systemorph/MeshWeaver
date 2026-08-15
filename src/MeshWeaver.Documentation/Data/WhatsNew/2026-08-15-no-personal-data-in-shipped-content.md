---
Name: No personal data in shipped content
Category: Fix
Description: Sample data and the built-in AI skills carried a real person's details — including a third party's email address and four stale API-token index entries. They no longer ship.
Icon: ShieldPerson
Order: -20260815
---

# No personal data in shipped content

Content that ships with the platform — sample data, the built-in agents and skills — is copied into
every install. Some of it carried details of real people.

- **Four API-token index entries** shipped as sample data. They hold no usable secret (a token is
  stored only as a one-way hash, and the tokens themselves were never in the sample set), and they
  pointed at a token store the samples do not even contain — so they were dead weight with a
  credential's shape. They are gone.
- **A third party's email address** appeared as the worked example in the built-in *group* skill,
  which walks through inviting someone and adding them to a group automatically. It now uses a
  placeholder address, as the rest of the examples always did.
- **One person's username** stood in as the example in several skills and the agent tool reference,
  while every other name in those same examples was already a placeholder.

Nothing about how any of this works has changed — the examples read the same, with names that belong
to nobody.
