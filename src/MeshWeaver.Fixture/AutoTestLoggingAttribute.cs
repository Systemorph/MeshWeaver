using Microsoft.Extensions.Logging;
using System.Reflection;
using Xunit.v3;

namespace MeshWeaver.Fixture;

/// <summary>
/// Automatically logs test method start and end markers to help correlate test execution with debug logs.
/// This attribute is automatically applied to all test methods through the TestBase class.
/// </summary>
public class AutoTestLoggingAttribute : BeforeAfterTestAttribute
{
    private static ILogger? _logger;

    static AutoTestLoggingAttribute()
    {
        try
        {
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddProvider(new DebugFileLoggerProvider());
                builder.SetMinimumLevel(LogLevel.Debug);
            });
            _logger = loggerFactory.CreateLogger("AutoTestLogging");
        }
        catch
        {
            _logger = null;
        }
    }
    
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        var testName = $"{methodUnderTest.DeclaringType?.Name}.{methodUnderTest.Name}";
        _logger?.LogInformation("=== TEST START: {TestMethod} ===", testName);
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        var testName = $"{methodUnderTest.DeclaringType?.Name}.{methodUnderTest.Name}";
        _logger?.LogInformation("=== TEST END: {TestMethod} ===", testName);
    }
}
