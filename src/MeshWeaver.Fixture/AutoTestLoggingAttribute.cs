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
   
    
    /// <summary>
    /// Runs before each test method, writing a "TEST START" marker to the active file output helper.
    /// </summary>
    /// <param name="methodUnderTest">The test method that is about to run.</param>
    /// <param name="test">The xUnit test being executed.</param>
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        var testName = $"{methodUnderTest.DeclaringType?.Name}.{methodUnderTest.Name}";
        var logMessage = $"=== TEST START: {testName} ===";
        
        
        // Also log to file output if available
        var fileOutput = XUnitFileOutputRegistry.GetAnyActiveOutputHelper();
        fileOutput?.SetCurrentTestMethod(methodUnderTest.Name);
        fileOutput?.WriteLine(logMessage);
    }

    /// <summary>
    /// Runs after each test method, writing any failure details and a "TEST END" marker
    /// to the active file output helper, then clearing the current test method.
    /// </summary>
    /// <param name="methodUnderTest">The test method that just ran.</param>
    /// <param name="test">The xUnit test that was executed.</param>
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
        fileOutput?.ClearCurrentTestMethod();
    }
}
