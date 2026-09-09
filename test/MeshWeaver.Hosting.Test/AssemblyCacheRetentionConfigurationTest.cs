using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.Hosting.Test;

public class AssemblyCacheRetentionConfigurationTest
{
    [Theory]
    [InlineData(null, 30)]
    [InlineData("7.00:00:00", 30)]
    [InlineData("30.00:00:00", 30)]
    [InlineData("60.00:00:00", 60)]
    [InlineData("invalid", 30)]
    public void Configuration_ExtendsHistory_WithoutShorteningItOrArmingDeletion(string? age, int days)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AssemblyCacheRetentionExtensions.MinimumAgeConfigKey] = age
        }).Build();

        var policy = AssemblyCacheRetentionExtensions.FromConfiguration(config);

        policy.MinimumAge.Should().Be(TimeSpan.FromDays(days));
        policy.Delete.Should().BeFalse();
    }
}
