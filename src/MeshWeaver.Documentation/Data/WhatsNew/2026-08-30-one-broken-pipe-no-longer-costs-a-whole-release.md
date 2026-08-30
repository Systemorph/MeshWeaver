---
Name: One broken pipe no longer costs a whole release
Category: Fix
Description: A container layer upload that lost its connection mid-push failed the portal image, and because publication is all-or-nothing that meant no release at all — twice in two hours, with a successful publish in between. The push now retries, bounded, and still fails red if the connection dies three times.
Icon: CloudArrowUp
Order: -20260830
---

# One broken pipe no longer costs a whole release

Publishing the platform is all-or-nothing on purpose: the promote step applies the real tags only
once **every** image leg has succeeded, so an install can never pull a half-shipped set. The cost of
that guarantee is that any single leg failing throws the whole release away.

On 2026-08-30 that happened twice in two hours, and not because anything was wrong with the code.
The `linux-arm64` image pushed cleanly; five minutes later the `linux-x64` leg's layer upload
reported **"Broken pipe"**, retried once inside the SDK, and gave up:

```
CONTAINER1001: Failed to upload blob using PATCH https://…/v2/memex-portal-ai/blobs/uploads/…
```

A release published successfully *between* the two failures, so this was not credentials, not a
quota, not a purge policy, and not the registry being unwell — just a connection that died partway
through a large upload, twice, on a link that is otherwise fine.

Meanwhile every self-updating install stayed on the previous image, because from the outside
"nothing published" looks exactly the same whether the cause was a real defect or a dropped TCP
connection.

## What changed

The portal image push now makes up to three attempts, re-authenticating with the registry between
them — a token can expire during a multi-architecture push that takes minutes per leg.

It is deliberately **not** a way to make failures go away. A compile error, a missing project or a
rejected credential fails identically on all three attempts and the step still ends red, pointing
at the last attempt's output. Only a connection that dies mid-upload gets a second chance, which is
the one thing a retry is actually for. And if the connection dies three times in a row, the error
says so in as many words — at that point it is no longer a transient and deserves investigating
rather than another retry.
