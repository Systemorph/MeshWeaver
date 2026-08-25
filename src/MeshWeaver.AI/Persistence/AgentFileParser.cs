using System.Text;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MeshWeaver.AI.Persistence;

/// <summary>
/// Parses .md files with YAML front matter containing nodeType: Agent into MeshNode objects with AgentConfiguration content.
/// The markdown body becomes the Instructions property of the AgentConfiguration.
/// </summary>
public class AgentFileParser : IFileFormatParser
{
    private const string AgentNodeType = "Agent";
    private const string DefaultAgentIcon = "Bot";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .Build();

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults)
        .Build();

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions => [".md"];

    /// <summary>
    /// Checks if the content is an Agent markdown file by peeking at the YAML frontmatter.
    /// </summary>
    public static bool IsAgentMarkdown(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var document = Markdig.Markdown.Parse(content, Pipeline);
        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        if (yamlBlock == null)
            return false;

        try
        {
            var yamlContent = yamlBlock.Lines.ToString();
            var frontMatter = YamlDeserializer.Deserialize<AgentFrontMatter>(yamlContent);
            return string.Equals(frontMatter?.NodeType, AgentNodeType, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public MeshNode? Parse(string filePath, string content, string relativePath)
    {
        // Derive id and namespace from path
        var (id, ns) = MarkdownNodePath.DeriveIdAndNamespace(relativePath);

        // Parse markdown to extract YAML front matter
        var document = Markdig.Markdown.Parse(content, Pipeline);
        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();

        AgentFrontMatter? frontMatter = null;
        if (yamlBlock != null)
        {
            try
            {
                var yamlContent = yamlBlock.Lines.ToString();
                frontMatter = YamlDeserializer.Deserialize<AgentFrontMatter>(yamlContent);
            }
            catch
            {
                // If YAML parsing fails, this isn't a valid agent file
                return null;
            }
        }

        // Only handle files with nodeType: Agent
        if (frontMatter == null || !string.Equals(frontMatter.NodeType, AgentNodeType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Extract markdown content (without YAML block) - this becomes Instructions
        var markdownContent = yamlBlock != null
            ? content.Substring(yamlBlock.Span.End + 1).TrimStart('\r', '\n')
            : content;

        // Get file last modified time — never 1601 for a missing file (see FileTimestamps).
        var lastModified = FileTimestamps.ObservedAt(filePath);

        // Build AgentConfiguration from frontmatter + markdown body. Node-level metadata
        // (name, description, icon, group, order) lives on the MeshNode below — NOT
        // duplicated on the content. See AgentConfiguration's class remarks.
        var agentConfig = new AgentConfiguration
        {
            Id = id,
            Description = frontMatter.Description,
            Instructions = string.IsNullOrWhiteSpace(markdownContent) ? null : markdownContent.Trim(),
            CustomIconSvg = frontMatter.CustomIconSvg,
            IsDefault = frontMatter.IsDefault,
            ExposedInNavigator = frontMatter.ExposedInNavigator,
            Delegations = frontMatter.Delegations?.Select(d => new AgentDelegation
            {
                AgentPath = d.AgentPath ?? "",
                Instructions = d.Instructions
            }).ToList(),
            Handoffs = frontMatter.Handoffs?.Select(h => new AgentHandoff
            {
                AgentPath = h.AgentPath ?? "",
                Instructions = h.Instructions
            }).ToList(),
            Plugins = frontMatter.Plugins?.Select(AgentPluginReference.Parse).ToList(),
            ContextMatchPattern = frontMatter.ContextMatchPattern,
            ModelTier = frontMatter.ModelTier,
            Translations = ReadTranslations(frontMatter.Translations)
        };

        var node = new MeshNode(id, ns)
        {
            NodeType = AgentNodeType,
            Name = frontMatter.Name ?? frontMatter.DisplayName ?? id,
            Description = frontMatter.Description,
            Category = frontMatter.GroupName ?? frontMatter.Category ?? "Agents",
            Icon = frontMatter.Icon ?? DefaultAgentIcon,
            Order = frontMatter.Order,
            State = ParseState(frontMatter.State),
            LastModified = lastModified,
            Content = agentConfig
        };

        return node;
    }

    /// <inheritdoc />
    public string Serialize(MeshNode node)
    {
        var sb = new StringBuilder();

        // Extract AgentConfiguration from node content
        AgentConfiguration? agentConfig = node.Content switch
        {
            AgentConfiguration config => config,
            System.Text.Json.JsonElement jsonElement => ExtractAgentConfigFromJsonElement(jsonElement),
            _ => null
        };

        // Build YAML front matter. Node-level metadata (name, description, group, icon,
        // order) comes from the MeshNode — the single source of truth — and only
        // agent-specific behaviour comes from the AgentConfiguration content. The node's
        // Category round-trips through the front-matter `groupName` key (the parser reads
        // groupName into Category).
        var frontMatter = new AgentFrontMatter
        {
            NodeType = AgentNodeType,
            Name = node.Name != node.Id ? node.Name : null,
            Description = node.Description,
            GroupName = node.Category is { Length: > 0 } and not "Agents" ? node.Category : null,
            Icon = node.Icon != DefaultAgentIcon ? node.Icon : null,
            Order = node.Order ?? 0,
            State = node.State != MeshNodeState.Active ? node.State.ToString() : null,

            // AgentConfiguration-specific properties
            IsDefault = agentConfig?.IsDefault ?? false,
            ExposedInNavigator = agentConfig?.ExposedInNavigator ?? false,
            ContextMatchPattern = agentConfig?.ContextMatchPattern,
            CustomIconSvg = agentConfig?.CustomIconSvg,
            Delegations = agentConfig?.Delegations?.Select(d => new DelegationFrontMatter
            {
                AgentPath = d.AgentPath,
                Instructions = d.Instructions
            }).ToList(),
            Handoffs = agentConfig?.Handoffs?.Select(h => new HandoffFrontMatter
            {
                AgentPath = h.AgentPath,
                Instructions = h.Instructions
            }).ToList(),
            Plugins = agentConfig?.Plugins?.Select(p => p.Methods is { Count: > 0 }
                ? $"{p.Name}:{string.Join(",", p.Methods)}"
                : p.Name).ToList(),
            ModelTier = agentConfig?.ModelTier,
            Translations = WriteTranslations(agentConfig?.Translations)
        };

        // Always write YAML block for agent files
        sb.AppendLine("---");
        var yaml = YamlSerializer.Serialize(frontMatter).TrimEnd();
        sb.AppendLine(yaml);
        sb.AppendLine("---");
        sb.AppendLine();

        // Append Instructions as markdown body
        var instructions = agentConfig?.Instructions;
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            sb.Append(instructions);
            if (!instructions.EndsWith('\n'))
            {
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public bool CanSerialize(MeshNode node)
    {
        // Handle nodes with NodeType "Agent" or AgentConfiguration content
        return node.NodeType == AgentNodeType
            || node.Content is AgentConfiguration
            || (node.Content is System.Text.Json.JsonElement je && HasAgentConfiguration(je));
    }

    /// <summary>
    /// Extracts AgentConfiguration from a JsonElement.
    /// </summary>
    private static AgentConfiguration? ExtractAgentConfigFromJsonElement(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        try
        {
            // Check for $type property to confirm it's an AgentConfiguration
            if (element.TryGetProperty("$type", out var typeProp) &&
                typeProp.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var typeValue = typeProp.GetString();
                if (typeValue != "AgentConfiguration" && !typeValue!.Contains("AgentConfiguration"))
                    return null;
            }

            // Extract required Id
            var id = ExtractString(element, "id") ?? "";

            // Node-level fields (displayName/icon/groupName/order) are no longer part of
            // AgentConfiguration — they live on the MeshNode. Only agent-specific fields
            // are extracted here.
            return new AgentConfiguration
            {
                Id = id,
                Description = ExtractString(element, "description"),
                Instructions = ExtractString(element, "instructions"),
                CustomIconSvg = ExtractString(element, "customIconSvg"),
                IsDefault = ExtractBool(element, "isDefault"),
                ExposedInNavigator = ExtractBool(element, "exposedInNavigator"),
                ContextMatchPattern = ExtractString(element, "contextMatchPattern"),
                ModelTier = ExtractString(element, "modelTier"),
                Delegations = ExtractDelegations(element),
                Handoffs = ExtractHandoffs(element),
                Plugins = ExtractPlugins(element),
                Translations = ExtractTranslations(element)
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractString(System.Text.Json.JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static bool ExtractBool(System.Text.Json.JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            return prop.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        return false;
    }

    /// <summary>
    /// Reads <c>translations</c> off an UNTYPED content element — the shape a node arrives in when
    /// the hub did not type it. Omitting it here would make the sync-back drop every translation a
    /// mesh-edited agent carries, silently and only on that path.
    /// </summary>
    private static IReadOnlyDictionary<string, LocalizedNodeText>? ExtractTranslations(
        System.Text.Json.JsonElement element)
    {
        if (!element.TryGetProperty("translations", out var prop)
            || prop.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        var map = new Dictionary<string, LocalizedNodeText>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in prop.EnumerateObject())
        {
            if (entry.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;
            map[entry.Name] = new LocalizedNodeText
            {
                Name = ExtractString(entry.Value, "name"),
                Description = ExtractString(entry.Value, "description"),
                Category = ExtractString(entry.Value, "category"),
            };
        }
        return map.Count == 0 ? null : map;
    }

    private static List<AgentDelegation>? ExtractDelegations(System.Text.Json.JsonElement element)
    {
        if (!element.TryGetProperty("delegations", out var delegationsProp) ||
            delegationsProp.ValueKind != System.Text.Json.JsonValueKind.Array)
            return null;

        var delegations = new List<AgentDelegation>();
        foreach (var item in delegationsProp.EnumerateArray())
        {
            var agentPath = ExtractString(item, "agentPath") ?? "";
            var instructions = ExtractString(item, "instructions");
            delegations.Add(new AgentDelegation { AgentPath = agentPath, Instructions = instructions });
        }

        return delegations.Count > 0 ? delegations : null;
    }

    private static List<AgentPluginReference>? ExtractPlugins(System.Text.Json.JsonElement element)
    {
        if (!element.TryGetProperty("plugins", out var pluginsProp) ||
            pluginsProp.ValueKind != System.Text.Json.JsonValueKind.Array)
            return null;

        var plugins = new List<AgentPluginReference>();
        foreach (var item in pluginsProp.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String:
                    plugins.Add(AgentPluginReference.Parse(item.GetString()!));
                    break;
                case System.Text.Json.JsonValueKind.Object:
                    var name = ExtractString(item, "name");
                    if (string.IsNullOrEmpty(name)) continue;
                    List<string>? methods = null;
                    if (item.TryGetProperty("methods", out var methodsProp) &&
                        methodsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                        methods = methodsProp.EnumerateArray()
                            .Where(m => m.ValueKind == System.Text.Json.JsonValueKind.String)
                            .Select(m => m.GetString()!)
                            .ToList();
                    plugins.Add(new AgentPluginReference { Name = name, Methods = methods });
                    break;
            }
        }

        return plugins.Count > 0 ? plugins : null;
    }

    private static List<AgentHandoff>? ExtractHandoffs(System.Text.Json.JsonElement element)
    {
        if (!element.TryGetProperty("handoffs", out var handoffsProp) ||
            handoffsProp.ValueKind != System.Text.Json.JsonValueKind.Array)
            return null;

        var handoffs = new List<AgentHandoff>();
        foreach (var item in handoffsProp.EnumerateArray())
        {
            var agentPath = ExtractString(item, "agentPath") ?? "";
            var instructions = ExtractString(item, "instructions");
            handoffs.Add(new AgentHandoff { AgentPath = agentPath, Instructions = instructions });
        }

        return handoffs.Count > 0 ? handoffs : null;
    }

    /// <summary>
    /// Checks if a JsonElement looks like it contains AgentConfiguration.
    /// </summary>
    private static bool HasAgentConfiguration(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
            return false;

        // Check for $type property indicating AgentConfiguration
        if (element.TryGetProperty("$type", out var typeProp) &&
            typeProp.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var typeValue = typeProp.GetString();
            return typeValue == "AgentConfiguration" || typeValue!.Contains("AgentConfiguration");
        }

        // Check for agent-specific properties
        return element.TryGetProperty("instructions", out _) &&
               (element.TryGetProperty("delegations", out _) || element.TryGetProperty("isDefault", out _));
    }

    /// <summary>
    /// Front matter → the typed translation map. Null for an absent or empty block, so an
    /// untranslated agent round-trips to a file with no <c>translations:</c> key.
    /// </summary>
    private static IReadOnlyDictionary<string, LocalizedNodeText>? ReadTranslations(
        Dictionary<string, LocalizedNodeTextFrontMatter>? fm)
    {
        if (fm is not { Count: > 0 })
            return null;
        var map = new Dictionary<string, LocalizedNodeText>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, text) in fm)
        {
            if (string.IsNullOrWhiteSpace(tag) || text is null)
                continue;
            map[tag.Trim()] = new LocalizedNodeText
            {
                Name = text.Name,
                Description = text.Description,
                Category = text.Category,
            };
        }
        return map.Count == 0 ? null : map;
    }

    /// <summary>The exact inverse of <see cref="ReadTranslations"/>.</summary>
    private static Dictionary<string, LocalizedNodeTextFrontMatter>? WriteTranslations(
        IReadOnlyDictionary<string, LocalizedNodeText>? translations)
    {
        if (translations is not { Count: > 0 })
            return null;
        var map = new Dictionary<string, LocalizedNodeTextFrontMatter>(StringComparer.Ordinal);
        foreach (var (tag, text) in translations)
            map[tag] = new LocalizedNodeTextFrontMatter
            {
                Name = text.Name,
                Description = text.Description,
                Category = text.Category,
            };
        return map;
    }

    private static MeshNodeState ParseState(string? state)
    {
        if (string.IsNullOrEmpty(state))
            return MeshNodeState.Active;

        return Enum.TryParse<MeshNodeState>(state, true, out var result)
            ? result
            : MeshNodeState.Active;
    }

    /// <summary>
    /// YAML front matter model for agent markdown files.
    /// </summary>
    private class AgentFrontMatter
    {
        // MeshNode standard properties
        public string? NodeType { get; set; }
        public string? Name { get; set; }
        public string? Category { get; set; }
        public string? Description { get; set; }
        public string? Icon { get; set; }
        public string? State { get; set; }

        // AgentConfiguration properties
        public string? DisplayName { get; set; }
        public string? GroupName { get; set; }
        public bool IsDefault { get; set; }
        public bool ExposedInNavigator { get; set; }
        public string? ContextMatchPattern { get; set; }
        public int Order { get; set; }
        public string? CustomIconSvg { get; set; }
        public string? ModelTier { get; set; }
        public List<DelegationFrontMatter>? Delegations { get; set; }
        public List<HandoffFrontMatter>? Handoffs { get; set; }
        public List<string>? Plugins { get; set; }

        /// <summary>
        /// <c>translations: { de: { name: …, description: …, category: … } }</c> — per-language
        /// overrides of the DISPLAY metadata only. The markdown body is the agent's system prompt
        /// and stays in one language; see <see cref="AgentConfiguration.Translations"/>.
        /// </summary>
        public Dictionary<string, LocalizedNodeTextFrontMatter>? Translations { get; set; }
    }

    /// <summary>One language's display overrides, as written in the front matter.</summary>
    private class LocalizedNodeTextFrontMatter
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
    }


    /// <summary>
    /// YAML model for delegation configuration.
    /// </summary>
    private class DelegationFrontMatter
    {
        public string? AgentPath { get; set; }
        public string? Instructions { get; set; }
    }

    /// <summary>
    /// YAML model for handoff configuration.
    /// </summary>
    private class HandoffFrontMatter
    {
        public string? AgentPath { get; set; }
        public string? Instructions { get; set; }
    }
}
