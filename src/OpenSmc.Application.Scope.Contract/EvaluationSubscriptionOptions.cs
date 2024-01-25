
namespace OpenSmc.Application.Scope
{
    public record EvaluationSubscriptionOptions
    {
        public EvaluationRefreshMode Mode { get; init; }
        public EvaluationSubscriptionOptions WithRefresh(EvaluationRefreshMode mode) => this with { Mode = mode };
        internal bool OmitEvent { get; init; }
        public EvaluationSubscriptionOptions WithOmitEvent(bool omitEvent) => this with { OmitEvent = omitEvent };
    }

    public enum EvaluationRefreshMode
    {
        Recompute, None
    }
}