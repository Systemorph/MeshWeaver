using MeshWeaver.Application.Styles;
using MeshWeaver.Domain;
using MeshWeaver.Utils;
using Microsoft.Extensions.AI;

namespace MeshWeaver.Reinsurance.AI;

public class FunctionProgressMessageAttribute : Attribute
{
    public virtual ProgressMessage GetMessage(FunctionCallContent content)
    {
        return new()
        {
            Message = $"{content.Name.Wordify()}...",
            Icon = FluentIcons.ChatSettings(IconSize.Size20)
        };
    }
}