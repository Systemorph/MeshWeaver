---
Name: A Reconcile adopts what the record renders
Category: Fix
Description: Re-applying an instance from its record stopped at helm when the cluster held a hand-made object the record now describes — helm refuses to take over what it never created. The operator now stamps Helm ownership on such objects before the upgrade, one by one and logged, and refuses one that belongs to another release.
Icon: Wrench
Order: -20260909
---

The first record-driven Reconcile of memex to reach helm, on 2026-09-09, failed like this:

```
UPGRADE FAILED: Unable to continue with update: SecretProviderClass "memex-kv" in namespace
"memex" exists and cannot be imported into the current release: invalid ownership metadata
```

`memex-kv` is the Key Vault class that feeds the portal its secrets. It was applied by hand in
August, long before the instance had a record, and no Helm release ever owned it. The record
describes it now — that is the point of the record — so the chart renders it, and helm, correctly,
will not overwrite an object it did not create. `--atomic` rolled the upgrade back; nothing changed.

The config repository's deploy lane already has an `adopt` action for this case, but it renders
the committed overlay, which never carried this object. What only the record renders can only be
adopted from the record.

## What it does now

Before `helm upgrade`, `hosting-deploy` renders the chart from the record and looks at every
resource it would manage. One that does not exist is left for helm to create. One this release
already owns is left alone. One that exists with no owner is adopted: two annotations and one
label, no change to its spec, nothing restarted — and from then on the record renders it like
everything else. One owned by **another** release is a refusal that stops the run before helm:
two releases claiming one object is how a teardown deletes the wrong instance's secret.

Every outcome is logged, and the run reports `adopted=<n>` to the mesh. The operator's test
suite plays a three-object estate through stubs and asserts what was stamped, in what order, that
a second run adopts nothing, and that the takeover case is refused with helm never reached.
