---
Name: A module built inside the platform image carries the image's own binding identity
Category: Fix
Description: The in-image module builder now stamps every module with the platform's own assembly version, so a module repository no longer has to hand-copy that number — and cannot ship one that binds to the wrong platform.
Icon: Checkmark
Order: -20260905
---

# A module built inside the platform image carries the image's own binding identity

A module compiled inside the platform image loads into that image's process and is bound by its
assemblies, so it must carry the platform's assembly version exactly. Until now that number had to
be written into the module repository as a literal, and checked after the fact. The day the
platform line moved from 3.0.0 to 3.1.0, every image build in the fleet went red on that literal;
a repository that tried to derive the number instead found the in-image builder runs no MSBuild
property functions, and every module came out `1.0.0.0`.

The builder now takes `--bind-to-image` and stamps `AssemblyVersion` and `FileVersion` with the
identity it reads out of the image itself, as immutable globals. The module-pack lane passes the
flag, probes the pinned tester for it and refuses with the pin to move when the tester predates it,
and still compares the emitted identity against the image's afterwards. A module repository's own
value is no longer consulted on that path; an explicit `-p:AssemblyVersion=` from a caller still
wins.
