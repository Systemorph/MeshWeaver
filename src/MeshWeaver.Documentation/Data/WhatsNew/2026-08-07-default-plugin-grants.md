---
Name: New installations get the platform plugins automatically
Category: What's New
Description: Registering a new MeshWeaver installation now grants it the platform plugin repo by default — no admin grant step before its Plugin Catalog fills.
Icon: Sparkle
---

# New installations get the platform plugins automatically

Until now, registering a new MeshWeaver installation gave it an identity but no entitlements: a
platform admin had to grant every source by hand before the new install's Plugin Catalog showed
anything. The registry operator can now opt sources into every new registration — typically the
platform plugin repo — and registration seeds those grants automatically.

The grant record stays the single authority: admins can still revoke or extend any instance
individually, and private or paid sources are never granted by default. The guided provisioning
plan in the Instances administration tab now walks through the plugin wiring as part of standing up
a new installation.
