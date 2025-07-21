using System.Reactive.Linq;
using MeshWeaver.Messaging;

namespace MeshWeaver.Activities;


public record CompleteActivityRequest(ActivityStatus? Status) : IRequest;

