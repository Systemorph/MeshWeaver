---
Name: Catalog action identity
Description: A catalog refresh must preserve the package and action addressed by a retained click.
---

# Catalog action identity

`ClickedEvent` carries a layout area path. `LayoutAreaHost` resolves that path against the
owner's **current** controls when it handles the event. A catalog row's position is therefore
not a safe identity: a package inserted or renamed between rendering and dispatch can occupy
the old row's path and receive its click.

Catalog cards now use the package ID, encoded as a single collision-free Base64URL path segment,
instead of an incrementing row number. Available packages and orphaned installation records have
separate prefixes. The install/update command and the orphan removal command also have explicit
area names, so an optional description cannot move a command to another address.

The existing owner dispatch remains authoritative. When the intended package disappears or its
install action is no longer available, the old action path resolves to no control and does
nothing. This change does not alter package sources, installation permissions, version selection,
or the installation engine.

## Evidence and verification

The original projection reproduced the defect through a real `LayoutAreaHost`: insert Package A
before Package B, then dispatch B's retained Install or Update path. Both cases fetched A.
The unchanged-order controls fetched B. The recording package source completed without emitting
any files, so these tests exercised the real owner dispatch without writing installed content.

`CatalogActionIdentityTest` lives in the existing Layout test suite. Ten cases cover Install and
Update with unchanged cards, insertion, a rename that changes ordering, and an optional
description, plus disappearing and newly installed packages. All ten pass with the correction;
the full Layout suite passes 486 tests with no failures or skips. The Release build passes with
warnings treated as errors.

The investigation followed an unintended local catalog installation whose click-time DOM and
owner trace were unavailable. The reproduced identity defect and a separate broad test locator
defect are established independently; neither is asserted to be the cause of that untraced click.

See [User Interface](../UserInterface) for layout ownership and
[Controls That Cannot Fail](../ControlsThatCannotFail) for control identity and binding rules.
