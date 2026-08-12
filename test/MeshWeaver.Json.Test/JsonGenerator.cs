using System.Text.Json.Nodes;

namespace MeshWeaver.Json.Test;

/// <summary>
/// Generates random JSON documents and structural mutations of them, for the property-style
/// <c>apply(diff(a,b)) == b</c> checks.
/// <para>
/// The key alphabet is chosen adversarially: it is dominated by the names that break naive
/// pointer handling — containing <c>/</c>, <c>~</c>, the literal <c>~0</c>/<c>~1</c> sequences,
/// the empty string, <c>-</c>, digits, quotes, backslashes and non-BMP characters. Everything the
/// hand-written case lists cover by example, this covers by construction.
/// </para>
/// </summary>
public static class JsonGenerator
{
    private static readonly string[] Keys =
    [
        "a", "b", "name", "value", "id", "$type", "content",
        "a/b", "a~b", "a~1b", "a~0b", "", "/", "~", "0", "1", "-",
        "äöü", "k😀", "\"quoted\"", "with space", "back\\slash", "\"acme/Docs/One\""
    ];

    private static readonly Func<JsonNode?>[] Scalars =
    [
        () => null,
        () => JsonValue.Create(0),
        () => JsonValue.Create(1),
        () => JsonValue.Create(-1),
        () => JsonValue.Create(1.5),
        () => JsonValue.Create(true),
        () => JsonValue.Create(false),
        () => JsonValue.Create(""),
        () => JsonValue.Create("x"),
        () => JsonValue.Create("äöü 😀"),
        () => JsonValue.Create(long.MaxValue),
        () => JsonValue.Create(0.1m),
    ];

    /// <summary>A random document — object, array or scalar at the root.</summary>
    public static JsonNode Document(Random random, int depth) => random.Next(depth >= 3 ? 8 : 10) switch
    {
        <= 5 => Object(random, depth),
        <= 7 => Array(random, depth),
        _ => Scalar(random) ?? JsonValue.Create(0)!
    };

    private static JsonObject Object(Random random, int depth)
    {
        var result = new JsonObject();
        var count = random.Next(0, 6);
        for (var i = 0; i < count; i++)
            result[Keys[random.Next(Keys.Length)]] = Child(random, depth);
        return result;
    }

    private static JsonArray Array(Random random, int depth)
    {
        var result = new JsonArray();
        var count = random.Next(0, 5);
        for (var i = 0; i < count; i++) result.Add(Child(random, depth));
        return result;
    }

    private static JsonNode? Child(Random random, int depth) =>
        depth >= 3
            ? Scalar(random)
            : random.Next(10) switch
            {
                <= 4 => Scalar(random),
                <= 7 => Object(random, depth + 1),
                _ => Array(random, depth + 1)
            };

    private static JsonNode? Scalar(Random random) => Scalars[random.Next(Scalars.Length)]();

    /// <summary>One to three structural edits: add/remove/retype a member, grow/shrink an array.</summary>
    public static JsonNode Mutate(Random random, JsonNode source, int depth)
    {
        var node = source.DeepClone();
        var edits = random.Next(1, 4);
        for (var i = 0; i < edits; i++) node = MutateOnce(random, node, depth);
        return node;
    }

    private static JsonNode MutateOnce(Random random, JsonNode node, int depth)
    {
        switch (node)
        {
            case JsonObject obj when obj.Count > 0 && random.Next(4) > 0:
                {
                    var keys = obj.Select(p => p.Key).ToArray();
                    var key = keys[random.Next(keys.Length)];
                    switch (random.Next(4))
                    {
                        case 0: obj.Remove(key); break;
                        case 1: obj[key] = Scalar(random); break;
                        case 2: obj[Keys[random.Next(Keys.Length)]] = Child(random, depth); break;
                        default:
                            obj[key] = obj[key] is { } child && depth < 3
                                ? MutateOnce(random, child.DeepClone(), depth + 1)
                                : Scalar(random);
                            break;
                    }
                    return obj;
                }
            case JsonObject empty:
                empty[Keys[random.Next(Keys.Length)]] = Child(random, depth);
                return empty;
            case JsonArray array when array.Count > 0:
                switch (random.Next(4))
                {
                    case 0: array.RemoveAt(random.Next(array.Count)); break;
                    case 1: array.Add(Child(random, depth)); break;
                    case 2: array[random.Next(array.Count)] = Scalar(random); break;
                    default:
                        {
                            var index = random.Next(array.Count);
                            array[index] = array[index] is { } child && depth < 3
                                ? MutateOnce(random, child.DeepClone(), depth + 1)
                                : Scalar(random);
                            break;
                        }
                }
                return array;
            case JsonArray emptyArray:
                emptyArray.Add(Child(random, depth));
                return emptyArray;
            default:
                return Document(random, depth);
        }
    }
}
