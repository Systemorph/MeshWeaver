namespace OpenSmc.Scopes.Test;

[InitializeScope(nameof(Init))]
public interface IInitializeScope : IMutableScope<int>
{
    int IntProperty { get; set; }
    void Init() => IntProperty = Identity;
}