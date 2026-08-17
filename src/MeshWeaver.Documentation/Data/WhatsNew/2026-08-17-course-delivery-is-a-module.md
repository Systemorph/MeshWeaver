---
Name: Course delivery is a module
Category: Feature
Description: The entitlement-gated course-asset route moved into its own MeshWeaver.Courses module, so a deployment that hosts no courses carries neither the resolver nor the route.
Icon: Library
Order: -20260817
---

# Course delivery is a module

`GET /assets/{Space}/…` — the route that resolves course media from a Space's synced repository,
applies the entitlement gate and redirects to a short-lived download URL — now lives in a
`MeshWeaver.Courses` module rather than being compiled into every portal.

Course delivery is a product concern, not a platform one. A deployment that hosts courses lists
the DLL; one that does not carries neither the resolver nor the route, and asks for neither the
GitHub App credentials nor the entitlement machinery.

Nothing changes for a portal that keeps it listed. The gate is unchanged — `Read` on the Space,
plus an entitlement when the course is paid, with course admins always passing — and so are the
`401` / `403` / `404` answers.

One deliberate detail worth recording: the route is mapped as explicitly anonymous. Module routes
are authenticated by default, which would have challenged an anonymous viewer before the gate
could run and turned a public course's assets into a login redirect. The gate itself distinguishes
"anonymous and allowed" from "not authenticated", so it stays the guard.
