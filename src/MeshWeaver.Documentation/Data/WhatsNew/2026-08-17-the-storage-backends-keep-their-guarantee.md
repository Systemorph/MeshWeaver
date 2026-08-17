---
Name: The storage backends keep their guarantee
Category: Fix
Description: Cosmos and Snowflake now have a test that loads them the way a portal does, so a broken publish layout fails CI instead of failing a deployment at boot.
Icon: ShieldCheckmark
Order: -20260817
---

# The storage backends keep their guarantee

Cosmos and Snowflake started shipping with the image so that `Graph:Storage:Type` could actually
select them. What shipped with them was a blind spot: nothing in the repo references either backend,
so nothing would have noticed their folder going wrong.

Two things that look like coverage are not. The compiler proves the **source** binds — it says
nothing about the publish **layout**, and the layout is where these two are fragile: their drivers
(the Cosmos client, the Snowflake driver with its Arrow/AWS/GCS closure) exist nowhere else in the
image, so a prune that took one away would leave an assembly that faults the first time a portal
touched it. And the emulator suites green-skip when their backend is unreachable, so they can pass
by not running.

There is now a test that loads each backend exactly the way a booting portal does — off disk, from
`modules/<Name>/`, with no compiled reference — and asserts the keyed storage factory that
`Graph:Storage:Type` resolves comes from that DLL. It needs no emulator, no endpoint and no network,
and runs in about 40 ms.

Nothing changes for a deployment that does not select one of these backends, which today is all of
them. For one that does, the guarantee is now checked on every commit rather than at its next
restart.
