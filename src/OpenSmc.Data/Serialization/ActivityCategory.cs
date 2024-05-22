using System.Reactive.Linq;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Activities;
using OpenSmc.Messaging;

namespace OpenSmc.Data.Serialization;

public static class ActivityCategory
{
    public const string DataUpdate = nameof(DataUpdate);
}
