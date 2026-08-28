---
Name: A commercial package no longer re-fails every boot
Category: Fix
Description: The unattended default install now records a commercial package as deliberately skipped, with the reason, instead of failing it at every restart with an error nobody could act on.
Icon: ShieldCheckmark
Order: -20260828
---

# A commercial package no longer re-fails every boot

When a deployment's default-install set swept in a commercial package — one with a price or a
sales contact — every restart tried to install it, was refused (correctly: an unattended boot has
no one to pay or approve), logged the refusal as an **error**, recorded the package as FAILED, and
promised "the next boot re-attempts it; that retry is the repair". For an authorization refusal
that promise was false: no retry can conjure an approver, so the same errors re-appeared at every
boot of every instance, forever.

The installer now classifies that refusal for what it is — **terminal for an unattended
installer** — before attempting anything:

- The package is recorded as **skipped**, with its reason, on the install ledger
  (`Plugins/_DefaultInstallLedger`) — one warning naming the packages and the remedy, instead of a
  per-boot error with a stack trace per package.
- Nothing re-attempts it. What changes the outcome is an event the next pass observes, not a
  retry — a Global Admin installs the package from the catalog, or the package stops being
  commercial. The classification is re-derived from the current catalog on every pass, so the
  moment a package's terms change it installs with no further ceremony.
- A genuine install *failure* (a timeout, a malformed package) keeps its existing behaviour:
  recorded as FAILED and retried next boot, because there a retry really is the repair.

If your instance should carry a commercial package, the ledger now tells you exactly that — and
that installing it from the catalog as a Global Admin is the way to get it.
