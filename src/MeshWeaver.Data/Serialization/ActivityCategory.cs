using System.Reactive.Linq;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Activities;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data.Serialization;

public static class ActivityCategory
{
    public const string DataUpdate = nameof(DataUpdate);
    public const string Import = nameof(Import);
}
