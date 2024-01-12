namespace OpenSmc.Messaging;


/// <summary>
/// TODO: Should we invert the concept, i.e. we start giving the path from outside in rather from inside out.
///
/// NotebookElement
/// {
/// host: Notebook{
///       host: Environment{
///           host: project
///        } 
///     }
/// }
///
///We should see to transform this into something like
///
/// .Project/Environment/Notebook/Element
/// 
/// </summary>
public interface IHostedAddress
{
    object Host { get; }
}

public interface IHostedAddressSettable : IHostedAddress
{
    object SetHost(object hostAddress);
}