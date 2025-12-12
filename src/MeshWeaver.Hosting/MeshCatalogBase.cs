using System.Reflection;
using System.Text.RegularExpressions;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

public abstract class MeshCatalogBase : IMeshCatalog
{
    public MeshConfiguration Configuration { get; }
    public IUnifiedPathRegistry PathRegistry { get; }
    private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
    private readonly MemoryCacheEntryOptions cacheOptions = new(){SlidingExpiration = TimeSpan.FromMinutes(5)};
    private readonly IMessageHub persistence;
    private readonly ILogger<MeshCatalogBase> logger;
    private readonly List<(MeshNamespace Namespace, Regex Regex)> compiledPatterns;

    protected MeshCatalogBase(IMessageHub hub, MeshConfiguration configuration, IUnifiedPathRegistry pathRegistry)
    {
        Configuration = configuration;
        PathRegistry = pathRegistry;
        logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshCatalogBase>>();
        persistence = hub.GetHostedHub(AddressExtensions.CreatePersistenceAddress())!;
        foreach (var node in Configuration.Nodes.Values)
                UpdateNode(node);

        // Pre-compile regex patterns for namespaces
        compiledPatterns = BuildPatterns();
    }

    private List<(MeshNamespace Namespace, Regex Regex)> BuildPatterns()
    {
        var patterns = new List<(MeshNamespace, Regex)>();

        // Add patterns from explicit namespaces
        foreach (var ns in Configuration.Namespaces.OrderBy(n => n.DisplayOrder))
        {
            var pattern = ns.Pattern ?? GenerateNamespacePattern(ns);
            var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            patterns.Add((ns, regex));
        }

        return patterns;
    }

    private static string GenerateNamespacePattern(MeshNamespace ns)
    {
        // Generate pattern based on Prefix and MinSegments
        // Pattern format: ^{prefix}(/segment){MinSegments}(/remainder)?$
        // Captures: address (full address), remainder (optional)
        var prefix = Regex.Escape(ns.Prefix);
        var segments = string.Concat(Enumerable.Repeat("/[^/]+", ns.MinSegments));
        return $@"^(?<address>{prefix}{segments})(?:/(?<remainder>.*))?$";
    }

    public async Task<MeshNode?> GetNodeAsync(Address address)
    {
        if (cache.TryGetValue(address.ToString(), out var ret))
            return (MeshNode?)ret;
        var node = Configuration.Nodes.GetValueOrDefault(address.ToString())
               ?? Configuration.Nodes.GetValueOrDefault(address.Type)
               ?? Configuration.MeshNodeFactories
                   .Select(f => f.Invoke(address))
                   .FirstOrDefault(x => x != null)
               ??
               await LoadMeshNode(address);
        if (node is null)
            return null;
        cache.Set(node.Key, node, cacheOptions);
        return UpdateNode(node);
    }

    private MeshNode UpdateNode(MeshNode node)
    {
        cache.Set(node.Key, node, cacheOptions);
        persistence.InvokeAsync(_ => UpdateNodeAsync(node), ex =>
        {
            logger.LogError(ex, "unable to update mesh catalog");
            return Task.CompletedTask;
        });
        return node;
    }

    protected abstract Task<MeshNode?> LoadMeshNode(Address address);


    public abstract Task UpdateAsync(MeshNode node);

    private readonly Dictionary<string, StreamInfo> channelTypes = new()
    {
        { AddressExtensions.AppType, new(StreamType.Channel, StreamProviders.Hub, ChannelNames.Hub) },
        { AddressExtensions.KernelType, new(StreamType.Channel, StreamProviders.Hub, ChannelNames.Hub) }
    };
    public Task<StreamInfo> GetStreamInfoAsync(Address address)
    {
        return Task.FromResult(channelTypes.GetValueOrDefault(address.Type) ?? new StreamInfo(StreamType.Stream, StreamProviders.Memory, address.ToString()));
    }


    protected abstract Task UpdateNodeAsync(MeshNode node);

    /// <inheritdoc />
    public Task<IReadOnlyList<MeshNamespace>> GetNamespacesAsync(CancellationToken ct = default)
    {
        // Return namespaces - these represent address types available for autocomplete
        var namespaces = Configuration.Namespaces
            .OrderBy(n => n.DisplayOrder)
            .ThenBy(n => n.Name)
            .ToList();

        return Task.FromResult<IReadOnlyList<MeshNamespace>>(namespaces);
    }

    /// <inheritdoc />
    public MeshNamespace? GetNamespace(string prefix)
    {
        return Configuration.Namespaces.FirstOrDefault(n =>
            string.Equals(n.Prefix, prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public AddressResolution? ResolveAddress(string addressType, string? id = null)
    {
        var address = string.IsNullOrEmpty(id)
            ? new Address(addressType)
            : new Address(addressType, id);

        return ResolvePath(address.ToString());
    }

    /// <inheritdoc />
    public AddressResolution? ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        // Normalize path - remove leading slash if present
        path = path.TrimStart('/');

        // 1. Try namespace patterns first (these have MinSegments defined)
        foreach (var (_, regex) in compiledPatterns)
        {
            var match = regex.Match(path);
            if (match.Success)
            {
                var addressStr = match.Groups["address"].Value;
                var remainder = match.Groups["remainder"].Success ? match.Groups["remainder"].Value : null;
                if (string.IsNullOrEmpty(remainder))
                    remainder = null;

                return new AddressResolution((Address)addressStr, remainder);
            }
        }

        // 2. Try matching against registered nodes using their Address (longest match first)
        var matchingAddress = Configuration.Nodes.Values
            .Select(node => node.Address)
            .Where(addr => PathStartsWithAddress(path, addr))
            .OrderByDescending(addr => addr.ToString().Length)
            .FirstOrDefault();

        if (matchingAddress != null)
            return new AddressResolution(matchingAddress, GetRemainder(path, matchingAddress));

        // 3. Try factories - build address incrementally and check if factory accepts it
        var parts = path.Split('/');
        for (var i = parts.Length; i >= 1; i--)
        {
            var candidateAddress = (Address)string.Join("/", parts.Take(i));
            var factoryMatch = Configuration.MeshNodeFactories
                .Select(f => f.Invoke(candidateAddress))
                .FirstOrDefault(x => x != null);

            if (factoryMatch != null)
            {
                var remainder = i < parts.Length ? string.Join("/", parts.Skip(i)) : null;
                return new AddressResolution(candidateAddress, remainder);
            }
        }

        return null;
    }

    private static bool PathStartsWithAddress(string path, Address address)
    {
        var addressStr = address.ToString();
        return path.Equals(addressStr, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(addressStr + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetRemainder(string path, Address address)
    {
        var addressStr = address.ToString();
        if (path.Length <= addressStr.Length)
            return null;
        return path.Substring(addressStr.Length + 1); // +1 to skip the /
    }
}
