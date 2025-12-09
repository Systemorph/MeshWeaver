## /agent

Switch to a different agent for subsequent messages.

**Usage:** `/agent @agent/Name` or `/agent Name`

### Description

The `/agent` command allows you to change which AI agent handles your messages. Once switched, the selected agent will handle all subsequent messages until you switch again.

### Examples

```
/agent @agent/InsuranceAgent
/agent InsuranceAgent
/agent insurance
```

### Notes

- Agent names are case-insensitive
- The agent selection persists until you explicitly switch to another agent
- You can also use `@agent/Name` inline in any message to temporarily address a specific agent
