namespace OpenSmc.Scopes
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class IdentityAggregationBehaviourAttribute : Attribute
    {
        public IdentityAggregationBehaviour Behaviour { get; }

        public IdentityAggregationBehaviourAttribute(IdentityAggregationBehaviour behaviour)
        {
            Behaviour = behaviour;
        }
    }

    public enum IdentityAggregationBehaviour
    {
        Default,
        Aggregate
    }
}