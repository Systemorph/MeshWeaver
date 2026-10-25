# MeshWeaver.Yaml

## Overview
The platform's own tolerant YAML reader and writer — YAML syntax, JSON semantics. YAML text is parsed
into a `System.Text.Json` `JsonNode` tree and bound with System.Text.Json under the same tolerant rules
the platform uses for JSON (case-insensitive keys, unknown keys ignored, numbers from strings, enums as
strings). No third-party dependency.

```csharp
var node = TolerantYaml.Parse(text);                  // JsonNode?
var config = TolerantYaml.Deserialize<MyConfig>(text); // tolerant binding
var yaml = TolerantYaml.Serialize(config);             // block-style YAML
```

See `Doc/Architecture/TolerantYaml` for the full semantics.
