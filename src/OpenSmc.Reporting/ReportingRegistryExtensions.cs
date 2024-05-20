using OpenSmc.Data;
using OpenSmc.Messaging;
using OpenSmc.Scopes;

namespace OpenSmc.Reporting;

public static class ReportingRegistryExtensions
{
    public static MessageHubConfiguration AddReporting(
        this MessageHubConfiguration configuration,
        Func<DataContext, DataContext> data,
        Func<ReportConfiguration, ReportConfiguration> reportConfiguration
    )
    {
        return configuration
            .AddData(data)
            .AddPlugin<ReportingPlugin>(p =>
                p.WithFactory(() => new(p.Hub, reportConfiguration(new())))
            );
    }

    public static MessageHubConfiguration AddReporting(
        this MessageHubConfiguration configuration,
        Func<DataContext, DataContext> data,
        Func<ReportConfigurationWithScopes, ReportConfigurationWithScopes> reportConfiguration
    ) =>
        configuration
            .WithServices(services => services.AddScopes())
            .AddData(data)
            .AddPlugin<ReportingPlugin>(p =>
                p.WithFactory(() => new(p.Hub, reportConfiguration(new())))
            );
}
