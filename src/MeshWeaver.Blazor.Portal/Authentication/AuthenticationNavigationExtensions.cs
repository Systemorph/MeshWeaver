using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Portal.Authentication;

/// <summary>
/// Extension methods for registering authentication navigation services.
/// </summary>
public static class AuthenticationNavigationExtensions
{
    /// <summary>
    /// Adds authentication navigation services with configuration from the specified section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAuthenticationNavigation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AuthenticationOptions>(
            configuration.GetSection(AuthenticationOptions.SectionName));

        services.AddScoped<IAuthenticationNavigationService, AuthenticationNavigationService>();

        return services;
    }

    /// <summary>
    /// Adds authentication navigation services with the specified options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Action to configure the options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAuthenticationNavigation(
        this IServiceCollection services,
        Action<AuthenticationOptions> configureOptions)
    {
        services.Configure(configureOptions);
        services.AddScoped<IAuthenticationNavigationService, AuthenticationNavigationService>();

        return services;
    }

    /// <summary>
    /// Adds authentication navigation services for a specific provider.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="provider">The provider name (e.g., "Dev", "MicrosoftIdentity", "Google").</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAuthenticationNavigation(
        this IServiceCollection services,
        string provider)
    {
        return services.AddAuthenticationNavigation(options => options.Provider = provider);
    }
}
