using System.Reflection;
using Xunit;
using Xunit.v3;

namespace MeshWeaver.Fixture;

/// <summary>
/// Automatically logs test method start and end markers to help correlate test execution with debug logs.
/// This attribute is automatically applied to all test methods through the TestBase class.
/// </summary>
public class AutoTestLoggingAttribute : BeforeAfterTestAttribute
{
   
    
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        var testName = $"{methodUnderTest.DeclaringType?.Name}.{methodUnderTest.Name}";
        var logMessage = $"=== TEST START: {testName} ===";
        
        
        // Also log to file output if available
        var fileOutput = XUnitFileOutputRegistry.GetAnyActiveOutputHelper();
        fileOutput?.WriteLine(logMessage);
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        // Also log to file output if available
        var fileOutput = XUnitFileOutputRegistry.GetAnyActiveOutputHelper();


        if (TestContext.Current.TestState?.Result == TestResult.Failed)
        {
            var message = $"""=== TEST FAILED: {string.Join("\n", TestContext.Current.TestState.ExceptionMessages ?? [])}""";
            fileOutput?.WriteLine(message);
        }

        var testName = $"{methodUnderTest.DeclaringType?.Name}.{methodUnderTest.Name}";
        var logMessage = $"=== TEST END: {testName} ===";
        
        
        fileOutput?.WriteLine(logMessage);
    }
}
