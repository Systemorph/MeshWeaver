## /model

Switch to a different AI model for subsequent messages.

**Usage:** `/model model:Name` or `/model Name`

### Description

The `/model` command allows you to change which AI model handles your messages. Once switched, the selected model will be used for all subsequent messages until you switch again.

### Examples

```
/model model:gpt-4o
/model gpt-4o
/model claude-3-opus
```

### Notes

- Model names are case-insensitive
- The model selection persists until you explicitly switch to another model
- You can also use `model:Name` inline in any message to temporarily use a specific model
- Available models depend on the configured AI providers
