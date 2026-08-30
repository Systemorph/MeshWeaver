using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace MeshWeaver.Testcontainers;

/// <summary>
/// A disposable memex — the portal image the platform BUILD produced, started as a test substrate
/// the way <c>Testcontainers.PostgreSql</c> starts a real Postgres (maintainer, 2026-08-30:
/// "clean test container package for memex … similar to pg"). The test is a client: it reaches
/// the instance over HTTP/MCP at <see cref="BaseAddress"/>, and the platform under test is
/// exactly the image the pipeline promoted — no core source checkout, no in-process mesh, no
/// <c>MeshWeaver.Fixture</c>.
///
/// <para><b>What the image needs, and what the builder sets.</b> The portal image runs
/// <c>Memex.Portal.Distributed</c> (Orleans + Postgres). A throwaway instance is single-node
/// (<c>Features:Orleans:Clustering=Localhost</c>), Azure-free (<c>Deployment:Backend=Filesystem</c>
/// with <c>Deployment:DataRoot=/data</c>), has dev login ON (the host forces it OFF unless the
/// value is literally <c>true</c>) and needs ONE Postgres for its data
/// (<c>ConnectionStrings:memex</c>) — which is why <see cref="MemexBuilder.WithPostgres(string)"/>
/// is required: the memex reaches Postgres INSIDE the Docker network, so hand it the alias-based
/// connection string of a Postgres container on the same network, never a host-mapped port.</para>
///
/// <para><b>The container's log is the test's log.</b> stdout/stderr are redirected to the test
/// output by default, so a failing test shows the portal's own lines beside it (maintainer:
/// "pls see we log out from container to github logging").</para>
/// </summary>
public sealed class MemexContainer(MemexConfiguration configuration) : DockerContainer(configuration)
{
    /// <summary>The instance's HTTP root as seen from the test host.</summary>
    public Uri BaseAddress =>
        new UriBuilder(Uri.UriSchemeHttp, Hostname, GetMappedPublicPort(MemexBuilder.HttpPort)).Uri;

    /// <summary>The MCP endpoint (<c>/mcp</c>).</summary>
    public Uri McpEndpoint => new(BaseAddress, "/mcp");

    /// <summary>The health endpoint the wait strategy polls (<c>/healthz</c>).</summary>
    public Uri HealthEndpoint => new(BaseAddress, MemexBuilder.HealthPath);
}

/// <summary>The memex-specific part of the container configuration.</summary>
public sealed class MemexConfiguration : ContainerConfiguration
{
    /// <summary>A fresh configuration with the memex-specific values.</summary>
    public MemexConfiguration(string? memexConnectionString = null)
    {
        MemexConnectionString = memexConnectionString;
    }

    /// <summary>Clones a resource configuration (Testcontainers' builder contract).</summary>
    public MemexConfiguration(IResourceConfiguration<CreateContainerParameters> resourceConfiguration)
        : base(resourceConfiguration)
    {
    }

    /// <summary>Clones a container configuration (Testcontainers' builder contract).</summary>
    public MemexConfiguration(IContainerConfiguration resourceConfiguration)
        : base(resourceConfiguration)
    {
    }

    /// <summary>Clones a memex configuration (Testcontainers' builder contract).</summary>
    public MemexConfiguration(MemexConfiguration resourceConfiguration)
        : this(new MemexConfiguration(), resourceConfiguration)
    {
    }

    /// <summary>Merges two configurations, the newer winning (Testcontainers' builder contract).</summary>
    public MemexConfiguration(MemexConfiguration oldValue, MemexConfiguration newValue)
        : base(oldValue, newValue)
    {
        MemexConnectionString = BuildConfiguration.Combine(oldValue.MemexConnectionString, newValue.MemexConnectionString);
    }

    /// <summary>The Postgres the instance stores its data in, as reachable FROM the container.</summary>
    public string? MemexConnectionString { get; }
}

/// <summary>
/// Builds a <see cref="MemexContainer"/>. The image is the consumer's to name — the pinned portal
/// image of the build under test (<c>meshweaver.azurecr.io/memex-portal-ai@sha256:…</c>); this
/// module never guesses a tag. A Postgres connection string is required.
/// </summary>
public sealed class MemexBuilder : ContainerBuilder<MemexBuilder, MemexContainer, MemexConfiguration>
{
    /// <summary>The port the portal listens on inside the container.</summary>
    public const ushort HttpPort = 8080;

    /// <summary>The health path the wait strategy polls.</summary>
    public const string HealthPath = "/healthz";

    /// <summary>Starts a builder with the memex defaults applied.</summary>
    public MemexBuilder() : this(new MemexConfiguration())
    {
        DockerResourceConfiguration = Init().DockerResourceConfiguration;
    }

    private MemexBuilder(MemexConfiguration resourceConfiguration) : base(resourceConfiguration)
    {
        DockerResourceConfiguration = resourceConfiguration;
    }

    /// <inheritdoc />
    protected override MemexConfiguration DockerResourceConfiguration { get; }

    /// <summary>
    /// The Postgres the memex stores its data in — <c>ConnectionStrings:memex</c>. The string must
    /// resolve FROM INSIDE the container: on a shared Docker network, use the Postgres container's
    /// network alias and port 5432, not the host-mapped port a test on the host would use.
    /// </summary>
    public MemexBuilder WithPostgres(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return Merge(DockerResourceConfiguration, new MemexConfiguration(memexConnectionString: connectionString))
            .WithEnvironment("ConnectionStrings__memex", connectionString);
    }

    /// <summary>
    /// Dev login on or off. On by default for a test instance; the host forces it OFF unless the
    /// configured value is literally <c>true</c>, so this always writes an explicit value.
    /// </summary>
    public MemexBuilder WithDevLogin(bool enabled = true) =>
        WithEnvironment("Authentication__EnableDevLogin", enabled ? "true" : "false");

    /// <inheritdoc />
    public override MemexContainer Build()
    {
        Validate();
        return new MemexContainer(DockerResourceConfiguration);
    }

    /// <inheritdoc />
    protected override MemexBuilder Init() =>
        base.Init()
            .WithPortBinding(HttpPort, assignRandomHostPort: true)
            .WithEnvironment("ASPNETCORE_HTTP_PORTS", HttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
            // Azure-free, single-node: the self-host filesystem backend and Localhost clustering
            // are the two switches that make the Distributed host run alone in one container.
            .WithEnvironment("Deployment__Backend", "Filesystem")
            .WithEnvironment("Deployment__DataRoot", "/data")
            .WithEnvironment("Features__Orleans__Clustering", "Localhost")
            .WithEnvironment("Authentication__EnableDevLogin", "true")
            // The container's log IS the test's log.
            .WithOutputConsumer(Consume.RedirectStdoutAndStderrToConsole())
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPort(HttpPort).ForPath(HealthPath)));

    /// <inheritdoc />
    protected override void Validate()
    {
        base.Validate();
        if (string.IsNullOrWhiteSpace(DockerResourceConfiguration.MemexConnectionString))
            throw new ArgumentException(
                "a memex needs a Postgres: call WithPostgres(<connection string reachable from inside "
                + "the container>) — the portal image runs Memex.Portal.Distributed, whose data lives "
                + "in ConnectionStrings:memex, and an instance without it does not start.",
                nameof(MemexConfiguration.MemexConnectionString));
    }

    /// <inheritdoc />
    protected override MemexBuilder Clone(IResourceConfiguration<CreateContainerParameters> resourceConfiguration) =>
        Merge(DockerResourceConfiguration, new MemexConfiguration(resourceConfiguration));

    /// <inheritdoc />
    protected override MemexBuilder Clone(IContainerConfiguration resourceConfiguration) =>
        Merge(DockerResourceConfiguration, new MemexConfiguration(resourceConfiguration));

    /// <inheritdoc />
    protected override MemexBuilder Merge(MemexConfiguration oldValue, MemexConfiguration newValue) =>
        new(new MemexConfiguration(oldValue, newValue));
}
