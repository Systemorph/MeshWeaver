using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public interface ILayout : IMessageHub
{
    void SetArea(string area, object value);

    void SetArea<T>(string area, Func<T> viewDefinition, Func<SetAreaOptions, SetAreaOptions> options = null);
    void SetArea<T>(string area, Func<Task<T>> viewDefinition, Func<SetAreaOptions, SetAreaOptions> options = null);
    void SetArea(string area, Func<Task<object>> viewDefinition, Func<SetAreaOptions, SetAreaOptions> options = null);

}

