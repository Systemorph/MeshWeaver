using OpenSmc.Data;
using OpenSmc.Messaging;

namespace OpenSmc.Reporting;

public static class ReportingRegistryExtensions
{
    public static MessageHubConfiguration AddReporting(
        this MessageHubConfiguration configuration,
        Func<DataContext, DataContext> data,
        Func<ReportConfiguration, ReportConfiguration> reportConfiguration)
    {
        return configuration
                .AddData(data)
                .AddPlugin(h => new ReportingPlugin(h, reportConfiguration))
            ;
    }
}