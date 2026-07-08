# Course cell-execution fixtures

These markdown files are **committed test fixtures** that pin the *executable-cell contract* for
interactive courses: every ` ```csharp --render <Area> ` cell a course ships must **compile** and
**run to Succeeded** on the same Roslyn kernel the portal uses. `CourseCellExecutionTest` enumerates
every executable cell under this folder and

1. compiles it against the kernel's default imports + `MeshScriptGlobals` (fast, no mesh), and
2. **executes** it through a real kernel session on a `MonolithMeshTestBase` mesh, asserting the
   `SubmitCodeResponse.Success` is `true` — the exact path a reader triggers when they open the page
   or press *Run*.

The point is that a bug like "the cells error on the live courses" is caught in CI, not by a reviewer.

## Why fixtures and not the live courses

The three authored courses (`Edu/Course` → `Edu/Module` → Theory / Example / `Edu/Exercise`) live on
the **memex mesh**, not in this repository. A CI test cannot (and must not) reach the live/prod mesh.
So this folder holds a small, representative slice of course content — modeled on the real `Edu`
authoring layout (`plugins/Edu/Guide.md`) — that travels with the repo and makes the contract
enforceable offline. When course content moves into the repo (e.g. via a committed GitSync export),
point the test's `CourseRoot` at that export and the same harness runs it unchanged.

## Layout (mirrors the Edu authoring structure)

```
CSharpBasics/
  Module01/Theory/…            Markdown lessons with --render cells (must run green)
  Module02/Theory/…            Markdown lessons with --render cells (must run green)
  Module02/Exercise/<Name>/    (an Edu/Exercise node)
    Statement.md               the task (prose)
    Solution/Solution.md       reference solution cells      → MUST run green
    Source/Starter.md          trainee starter (stub) cells  → MUST compile; may throw at runtime
```

The `Source/` (starter) · `Test/` (validation spec) · `Solution/` (reference) split mirrors the real
Edu authoring layout (`plugins/Edu/Guide.md`).

## The Solution vs Exercise-stub rule

- Cells under a **`Solution/`** path are reference solutions — they must **execute green** (this wins
  even though the file sits under the exercise's `Exercise/` container).
- Cells under a **`Source/`** (Starter) or **`Test/`** (Validation spec) path are trainee stubs — they
  must **compile**, but they may fail only on their intended assertion (an incomplete stub throws by
  design). The test asserts compile-success for those, not runtime-green.
