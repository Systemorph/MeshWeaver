---
Name: Every release is scanned with OWASP ZAP before it is tagged
Category: Feature
Description: A release now carries a security scan record — an OWASP ZAP public active scan and an authenticated passive baseline against the deployment serving the candidate — with the verdict and every finding's disposition on the release notes page.
Icon: ShieldCheckmark
Order: -20260906
---

# Every release is scanned with OWASP ZAP before it is tagged

A release is a promotion of a tested continuous build, and one more thing is now tested before
the tag goes on: the deployment serving that build is scanned with OWASP ZAP (the Zed Attack
Proxy), twice. A public active scan attacks everything an anonymous visitor can reach; an
authenticated passive baseline walks the signed-in portal with a real session and inspects every
response without firing a payload, which is what reaches the bundles only a signed-in page loads.

The verdict is written on the release notes page: both runs must report zero failures, a shipped
library with published advisories blocks the tag outright, and every other warning is either
fixed on the candidate or carried with its reason and tracking issue. The 3.0.0 notes are the first
page with that record — including the one finding it fixed, a sanitiser with cross-site-scripting
advisories inside the code editor's bundle. The procedure, the commands and what the scanner
cannot see are on [OWASP ZAP Scan — Every Release](/Doc/Architecture/SecurityScanning).
