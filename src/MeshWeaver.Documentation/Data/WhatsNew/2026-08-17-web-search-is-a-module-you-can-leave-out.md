---
Name: Web search is a module you can leave out
Category: Feature
Description: The agent web-search tools moved into their own MeshWeaver.AI.WebSearch module, so a deployment decides by listing whether agent turns can reach the public internet at all.
Icon: Globe
Order: -20260817
---

# Web search is a module you can leave out

`SearchWeb`, `FetchWebPage` and the feed readers now live in their own `MeshWeaver.AI.WebSearch`
module instead of inside the AI assembly every deployment ships. Whether agents can reach the
public internet is now a composition decision — list the DLL under `Modules:Assemblies`, or don't.

Nothing changes for a deployment that keeps the module listed, which is every portal today: the
tools bind the same `WebSearch` configuration section and still advertise nothing at all until a
backend is configured, so a listed-but-unconfigured deployment behaves exactly as before.

What made this a clean cut is that agent tool plugins have always resolved **by name** rather than
by type: an agent asks for `WebSearch` in its frontmatter, and the factory that assembles its
tools never mentions the implementation. So the whole family could move out without leaving a
seam behind — and an agent that declares `WebSearch` where the module is absent simply gets no
such tool, exactly like an unconfigured one.
