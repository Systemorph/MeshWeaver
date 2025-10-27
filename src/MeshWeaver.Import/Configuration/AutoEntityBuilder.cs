using System.Reflection;

namespace MeshWeaver.Import.Configuration;

/// <summary>
/// Helper class to automatically build entities from property dictionaries using reflection.
/// </summary>
public static class AutoEntityBuilder
{
    /// <summary>
    /// Creates an entity builder function for a given type using reflection.
    /// </summary>
    /// <param name="type">The type to instantiate</param>
    /// <returns>A function that builds instances from property dictionaries</returns>
    public static Func<Dictionary<string, object?>, object> CreateBuilder(Type type)
    {
        return properties =>
        {
            var instance = Activator.CreateInstance(type);
            if (instance == null)
                throw new InvalidOperationException($"Failed to create instance of type {type.FullName}");

            foreach (var (key, value) in properties)
            {
                var prop = type.GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null || !prop.CanWrite)
                    continue;

                try
                {
                    var convertedValue = ConvertValue(value, prop.PropertyType);
                    prop.SetValue(instance, convertedValue);
                }
                catch
                {
                    // Ignore conversion errors for individual properties
                }
            }

            return instance;
        };
    }

    /// <summary>
    /// Creates a typed entity builder function.
    /// </summary>
    public static Func<Dictionary<string, object?>, T> CreateBuilder<T>() where T : class
    {
        var builder = CreateBuilder(typeof(T));
        return properties => (T)builder(properties);
    }

    /// <summary>
    /// Converts a value to the target type with support for common conversions.
    /// </summary>
    private static object? ConvertValue(object? value, Type targetType)
    {
        if (value == null)
            return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

        // If already correct type
        if (targetType.IsInstanceOfType(value))
            return value;

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // Empty strings should be treated as null/default
        if (value is string s && string.IsNullOrWhiteSpace(s))
            return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

        // Handle string conversions
        if (underlying == typeof(string))
            return value.ToString();

        // Handle numeric conversions
        if (underlying == typeof(int))
        {
            if (value is int i) return i;
            if (value is decimal dm) return (int)dm;
            if (value is double d) return (int)d;
            if (int.TryParse(value.ToString(), out var parsed)) return parsed;
        }

        if (underlying == typeof(double))
        {
            if (value is double d) return d;
            if (value is decimal dm) return (double)dm;
            if (value is int i) return (double)i;
            if (double.TryParse(value.ToString(), out var parsed)) return parsed;
        }

        if (underlying == typeof(decimal))
        {
            if (value is decimal dm) return dm;
            if (value is double d) return (decimal)d;
            if (decimal.TryParse(value.ToString(), out var parsed)) return parsed;
        }

        if (underlying == typeof(bool))
        {
            if (value is bool b) return b;
            var str = value.ToString()?.Trim().ToLowerInvariant();
            if (str == "true" || str == "yes" || str == "1") return true;
            if (str == "false" || str == "no" || str == "0") return false;
        }

        // Fallback to Convert.ChangeType
        if (value is IConvertible)
            return Convert.ChangeType(value, underlying);

        return value;
    }
}
