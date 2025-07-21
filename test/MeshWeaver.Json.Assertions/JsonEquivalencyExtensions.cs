using System;
using FluentAssertions.Equivalency;

namespace MeshWeaver.Json.Assertions;

public static class JsonEquivalencyExtensions
{
    public static EquivalencyOptions<T> UsingJson<T>(this EquivalencyOptions<T> options, Func<JsonEquivalencyOptions, JsonEquivalencyOptions>? jsonOptionsConfig = null)
    {
        var jsonOptions = new JsonEquivalencyOptions();

        if (jsonOptionsConfig != null)
            jsonOptions = jsonOptionsConfig(jsonOptions);

        return options
               .Using(new JsonEquivalency(jsonOptions))
            ;
    }
}
