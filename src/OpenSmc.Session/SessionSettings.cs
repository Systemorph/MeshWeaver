namespace OpenSmc.Session;

public class SessionSettings
{
    public Dictionary<string, SessionTierSetting> Tiers { get; set; } = new();
    public List<ImageSettings> Images { get; set; } = new();
    public RequestedSpecification Default { get; set; }
    public RequestedSpecification Legacy { get; set; }

    //kubernetes specific
    public AllocationOptions AllocationOptions { get; set; }

    public TimeSpan PodStartingTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan InitializingTimeout { get; set; } = TimeSpan.FromSeconds(120);
    public TimeSpan PodStoppingTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public int MaxSessionPingFailures { get; set; } = 4;
    public TimeSpan SessionMonitorPeriod { get; set; } = TimeSpan.FromSeconds(5);
}

public record RequestedSpecification
{
    public string StartScript { get; init; }
    public string Tier { get; init; }
    public double? Cpu { get; init; }
    public string Image { get; init; }
    public string ImageTag { get; init; }
    public TimeSpan? SessionIdleTimeout { get; init; }
    public TimeSpan? ApplicationIdleTimeout { get; init; }
}

public class SessionTierSetting
{
    public string SystemName { get; set; }
    public string DisplayName { get; set; }
    public double MinRatio { get; set; }
    public ResourceLimits Limits { get; set; }
    public ResourceLimits Requests { get; set; }
    public Dictionary<string, string> NodeSelector { get; set; } = new();
    public Toleration[] Toleration { get; set; }
    public double CreditsPerMinute { get; set; }
}

public class Toleration
{
    public string Effect { get; set; }
    public string Key { get; set; }
    public string Operator { get; set; }
    public long? TolerationSeconds { get; set; }
    public string Value { get; set; }
}

public class ResourceLimits
{
    public double Cpu { get; set; } // in cores. Example if value is 12 ==> it corresponds to 12000m in kubernetes terms
    public double Memory { get; set; } // in Gi. Example if value is 16 ==> it corresponds  16Gi in kubernetes terms
}

public class ImageSettings
{
    public string Image { get; set; }
    public string DisplayName { get; set; }
}

public class AllocationOptions
{
    public Dictionary<string, string> SessionLabels { get; set; }
    public string ImagePullSecrets { get; set; }
    public string HostingStrategy { get; set; }
    public string ConfigMapName { get; set; }
    public string ServiceAccount { get; set; }
}
