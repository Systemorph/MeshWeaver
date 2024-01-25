namespace OpenSmc.Scopes
{
    [AttributeUsage(AttributeTargets.Property)]
    public class DirectEvaluationAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Interface)]
    public class InitializeScopeAttribute : Attribute
    {
        public string MethodName { get; }

        public InitializeScopeAttribute(string methodName)
        {
            MethodName = methodName;
        }
    }
}