---
Name: The Workflow Expression Limit
Category: Architecture
Description: GitHub compiles any workflow string holding a `${{ }}` (a whole run script included) into one expression and refuses the ENTIRE workflow, at load and with no job, once it passes 21,000 characters. How one shared lane took every repository's CI down, how to tell this failure apart, and the rule that keeps it from recurring.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 12h16"/><path d="M16 6l6 6-6 6"/><path d="M2 4v16"/></svg>
---

# The Workflow Expression Limit

## 🚨 The rule

**Never write `${{ … }}` inside a `run:` script. Pass the value through the step's `env:` and read `$NAME` in the script.**

A string without `${{` is plain text to GitHub, and no expression limit applies to it. A string *with* one, however long, is compiled into ONE expression, and GitHub caps a compiled expression at **21,000 characters**. Past that it refuses the whole workflow file:

> Exceeded max expression length 21000, (Line: 2627, Col: 14)

`check-workflow-expression-length.py` (core's `dotnet-test.yml`, beside the other workflow checks) fails any string that holds a `${{` and is longer than 15,000 raw characters. The margin is deliberate. The compiled form is longer than the raw text, by an amount the script does not model: the failing script below was 20,589 raw characters, and a naive estimate of its compiled length still came out under 21,000. **The fix for a red is never to raise the cap.** Move the expression to `env:`.

## How to recognise it

- **The run has no job at all**, and it is `failure` within seconds of being created.
- **The run is named after the FILE** (`.github/workflows/ci.yml`), not the workflow's `name:`. GitHub could not read the name, because it refused the file.
- **`gh run view` says only** "This run likely failed because of a workflow file issue". The REST API carries no error text. The run page in a browser shows the message above, with the line and column.
- **The line can be in a reusable workflow, not the caller's file.** A lane called `@main` is compiled into every caller, so one change to a shared lane in this repository refuses every caller's workflow at once.
- **actionlint passes.** It does not check this limit.

## The incident (2026-11-16)

#8322 added a compile-cache block to the gate step of `node-repo-gate.yml`. That step's script carried one expression, `if [ "${{ inputs.capture-coredumps }}" = "true" ]`, and the block grew it from 18,886 to 20,589 characters. It merged at 22:57Z. From 23:03Z, every `ci.yml` that calls the lane `@main` failed at load with no job: MeshWeaver.Plugins and every satellite, on pushes to `main`, on pull requests and on the nightly schedule. Nothing published, and nothing could merge, for about eight hours.

It was proven, not inferred. Three throwaway caller workflows on #8340 (each with a job that never ran) called the lane at the last good commit, at the broken `main` and at the fix. GitHub accepted the first and the third and refused the second with the message above, pointing at the gate step's script.

The fix moved `inputs.capture-coredumps` into the step's `env:` as `CAPTURE_COREDUMPS`. It also moved the one expression in `node-repo-module-pack.yml`'s build step (17,055 characters, the next largest) into `env:` as `LANE_KEY`. After both, no workflow in core or MeshWeaver.Plugins holds an expression in a string longer than 15,000 characters.

## Related

- [Duplicate Keys in Workflow YAML](../WorkflowDuplicateKeys): the other way a workflow file can be wrong while every reader of it looks fine.
- [Reading CI Signals](../ReadingCiSignals): what a check's colour means, and when a red is infrastructure.
