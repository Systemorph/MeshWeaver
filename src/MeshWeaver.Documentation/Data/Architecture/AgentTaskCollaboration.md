---
Name: Agent Task Collaboration
Description: "The first-class MCP contract for launching a shared AgentTask, observing its participant threads, and handling immutable participant settings."
---

# Agent Task Collaboration

Use the MCP `start_collaboration` operation to launch a multi-agent task. It creates one
first-class `AgentTask` and one ordinary thread per participant below
`{taskPath}/_Thread`. Do **not** create or patch `AgentTask` or `Thread` nodes directly: the
operation owns task creation, thread allocation, and initial message submission.

## Starting a task

Pass a shared goal, an owning namespace, a human-readable title, and a participant array. Every
participant supplies a role and its initial message; `agentName`, `harness`, `modelName`, and
`effort` select the harness run at creation time. The response is the evidence that work actually
started: record its `taskPath` and every returned `threadPath`.

```text
start_collaboration(
  namespacePath: "rbuergi",
  title: "Evidence-backed issue closure",
  goal: "Find root causes, ship verified fixes, and record the evidence.",
  participants: [
    { role: "bug fixer", harness: "ClaudeCode", modelName: "Opus 5", effort: "high", ... },
    { role: "reviewer", harness: "Codex", modelName: "gpt-5.6-terra", effort: "max", ... }
  ])
```

Each participant can discover the sibling threads below the task path. Read the task node and the
participant threads to observe their state and transcript; use the sanctioned message-submission
operation for a follow-up, never a direct node write.

## Participant settings are creation-time settings

`effort`, harness, agent, and model selection are captured on the participant when
`start_collaboration` creates the task. The follow-up message operation accepts a thread path,
message, and optional agent override; it has **no** effort, model, or harness parameter. There is
also no supported participant-update operation in the MCP contract.

Consequently, do not claim that a running participant was changed from `high` to `max` merely
because it received a message asking it to reason harder. That is an instruction, not a scheduler
reconfiguration. Do not patch the task or thread to force the value: bypassing the lifecycle
operation risks an incoherent task/thread relationship.

If the execution configuration must change, record the current task and thread paths, coordinate
the hand-off so work is not duplicated, then launch a replacement collaboration through
`start_collaboration` with the desired settings. The earlier task remains an independent active
task until the platform offers a supported stop or reconfiguration operation; never silently
assume it was replaced.

## Delivery remains the task's definition of done

For a MeshWeaver issue, collaboration is only the coordination mechanism. A closure still requires
evidence of the deterministic reproduction and root cause, an isolated worktree, Release
warnings-as-errors validation, reviewed and green CI, merge, supported deployment, and live
verification. The task's summary must distinguish work that is triaged, fixed locally, merged,
deployed, and actually verified in the target instance.
