// <meshweaver>
// Id: Testing/Hosting/AssemblyCacheRetentionConfigurationTest
// DisplayName: Testing/Hosting/AssemblyCacheRetentionConfigurationTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

public class AssemblyCacheRetentionConfigurationTest
{
    [MeshTheory]
    [MeshInlineData(null, 30)]
    [MeshInlineData("7.00:00:00", 30)]
    [MeshInlineData("30.00:00:00", 30)]
    [MeshInlineData("60.00:00:00", 60)]
    [MeshInlineData("invalid", 30)]
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
