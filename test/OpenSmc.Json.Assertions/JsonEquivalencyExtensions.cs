using FluentAssertions.Equivalency;

namespace OpenSmc.Json.Assertions;

public static class JsonEquivalencyExtensions
{
    public static EquivalencyAssertionOptions<T> UsingJson<T>(this EquivalencyAssertionOptions<T> options, Func<JsonEquivalencyOptions, JsonEquivalencyOptions> jsonOptionsConfig = null)
    {
        var jsonOptions = new JsonEquivalencyOptions();

        if (jsonOptionsConfig != null)
            jsonOptions = jsonOptionsConfig(jsonOptions);

        return options
               .Using(new JsonEquivalency(jsonOptions))
            ;
    }
}