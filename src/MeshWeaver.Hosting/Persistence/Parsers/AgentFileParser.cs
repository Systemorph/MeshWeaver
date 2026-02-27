using System.Text;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MeshWeaver.Hosting.Persistence.Parsers;

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

    public Task<MeshNode?> ParseAsync(string filePath, string content, string relativePath, CancellationToken ct = default)
    {
        // Derive id and namespace from path
        var (id, ns) = DeriveIdAndNamespace(relativePath, filePath);

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
                return Task.FromResult<MeshNode?>(null);
            }
        }

        // Only handle files with nodeType: Agent
        if (frontMatter == null || !string.Equals(frontMatter.NodeType, AgentNodeType, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<MeshNode?>(null);
        }

        // Extract markdown content (without YAML block) - this becomes Instructions
        var markdownContent = yamlBlock != null
            ? content.Substring(yamlBlock.Span.End + 1).TrimStart('\r', '\n')
            : content;

        // Get file last modified time
        var fileInfo = new FileInfo(filePath);
        var lastModified = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);

        // Build AgentConfiguration from frontmatter + markdown body
        var agentConfig = new AgentConfiguration
        {
            Id = id,
            DisplayName = frontMatter.Name ?? frontMatter.DisplayName ?? id,
            Description = frontMatter.Description,
            Instructions = string.IsNullOrWhiteSpace(markdownContent) ? null : markdownContent.Trim(),
            Icon = frontMatter.Icon,
            CustomIconSvg = frontMatter.CustomIconSvg,
            GroupName = frontMatter.GroupName,
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
            PreferredModel = frontMatter.PreferredModel,
            ContextMatchPattern = frontMatter.ContextMatchPattern,
            Order = frontMatter.Order
        };

        var node = new MeshNode(id, ns)
        {
            NodeType = AgentNodeType,
            Name = frontMatter.Name ?? frontMatter.DisplayName ?? id,
            Category = frontMatter.Category ?? "Agents",
            Icon = frontMatter.Icon ?? DefaultAgentIcon,
            State = ParseState(frontMatter.State),
            LastModified = lastModified,
            Content = agentConfig
        };

        return Task.FromResult<MeshNode?>(node);
    }

    public Task<string> SerializeAsync(MeshNode node, CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        // Extract AgentConfiguration from node content
        AgentConfiguration? agentConfig = node.Content switch
        {
            AgentConfiguration config => config,
            System.Text.Json.JsonElement jsonElement => ExtractAgentConfigFromJsonElement(jsonElement),
            _ => null
        };

        // Build YAML front matter from node properties and AgentConfiguration
        var frontMatter = new AgentFrontMatter
        {
            NodeType = AgentNodeType,
            Name = node.Name != node.Id ? node.Name : null,
            Category = node.Category != "Agents" ? node.Category : null,
            Icon = node.Icon != DefaultAgentIcon ? node.Icon : null,
            State = node.State != MeshNodeState.Active ? node.State.ToString() : null,

            // AgentConfiguration-specific properties
            Description = agentConfig?.Description,
            DisplayName = agentConfig?.DisplayName != node.Name ? agentConfig?.DisplayName : null,
            GroupName = agentConfig?.GroupName,
            IsDefault = agentConfig?.IsDefault ?? false,
            ExposedInNavigator = agentConfig?.ExposedInNavigator ?? false,
            ContextMatchPattern = agentConfig?.ContextMatchPattern,
            PreferredModel = agentConfig?.PreferredModel,
            Order = agentConfig?.Order ?? 0,
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
            }).ToList()
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

        return Task.FromResult(sb.ToString());
    }

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

            return new AgentConfiguration
            {
                Id = id,
                DisplayName = ExtractString(element, "displayName"),
                Description = ExtractString(element, "description"),
                Instructions = ExtractString(element, "instructions"),
                Icon = ExtractString(element, "icon") ?? ExtractString(element, "iconName"),
                CustomIconSvg = ExtractString(element, "customIconSvg"),
                GroupName = ExtractString(element, "groupName"),
                IsDefault = ExtractBool(element, "isDefault"),
                ExposedInNavigator = ExtractBool(element, "exposedInNavigator"),
                PreferredModel = ExtractString(element, "preferredModel"),
                ContextMatchPattern = ExtractString(element, "contextMatchPattern"),
                Order = ExtractInt(element, "order"),
                Delegations = ExtractDelegations(element),
                Handoffs = ExtractHandoffs(element)
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

    private static int ExtractInt(System.Text.Json.JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            return prop.GetInt32();
        }
        return 0;
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

    private static (string Id, string? Namespace) DeriveIdAndNamespace(string relativePath, string filePath)
    {
        // Remove extension and normalize
        var pathWithoutExt = relativePath;
        if (pathWithoutExt.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            pathWithoutExt = pathWithoutExt[..^3];

        pathWithoutExt = pathWithoutExt.Trim('/').Replace('\\', '/');

        var lastSlash = pathWithoutExt.LastIndexOf('/');
        if (lastSlash < 0)
            return (pathWithoutExt, null);

        var ns = pathWithoutExt[..lastSlash];
        var id = pathWithoutExt[(lastSlash + 1)..];
        return (id, ns);
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
        public string? PreferredModel { get; set; }
        public int Order { get; set; }
        public string? CustomIconSvg { get; set; }
        public List<DelegationFrontMatter>? Delegations { get; set; }
        public List<HandoffFrontMatter>? Handoffs { get; set; }
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
