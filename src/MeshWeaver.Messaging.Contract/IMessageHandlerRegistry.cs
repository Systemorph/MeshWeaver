namespace MeshWeaver.Messaging;

/// <summary>
/// Registry of message handlers for a hub. Each <c>Register</c> overload installs a
/// handler (optionally filtered) for a message type and returns an
/// <see cref="IDisposable"/> that unregisters it when disposed.
/// </summary>
public interface IMessageHandlerRegistry
{
    /// <summary>
    /// Registers a synchronous handler for messages of type <typeparamref name="TMessage"/>.
    /// </summary>
    /// <typeparam name="TMessage">The message type to handle.</typeparam>
    /// <param name="action">The handler to invoke.</param>
    /// <returns>A handle that unregisters the handler when disposed.</returns>
    IDisposable Register<TMessage>(SyncDelivery<TMessage> action);
    /// <summary>
    /// Registers a reactive handler for messages of type <typeparamref name="TMessage"/>.
    /// </summary>
    /// <typeparam name="TMessage">The message type to handle.</typeparam>
    /// <param name="action">The handler to invoke.</param>
    /// <returns>A handle that unregisters the handler when disposed.</returns>
    IDisposable Register<TMessage>(AsyncDelivery<TMessage> action);
    /// <summary>
    /// Registers a synchronous, filtered handler for messages of type <typeparamref name="TMessage"/>.
    /// </summary>
    /// <typeparam name="TMessage">The message type to handle.</typeparam>
    /// <param name="action">The handler to invoke.</param>
    /// <param name="filter">Predicate selecting which deliveries the handler receives.</param>
    /// <returns>A handle that unregisters the handler when disposed.</returns>
    IDisposable Register<TMessage>(SyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter);
    /// <summary>
    /// Registers a reactive, filtered handler for messages of type <typeparamref name="TMessage"/>.
    /// </summary>
    /// <typeparam name="TMessage">The message type to handle.</typeparam>
    /// <param name="action">The handler to invoke.</param>
    /// <param name="filter">Predicate selecting which deliveries the handler receives.</param>
    /// <returns>A handle that unregisters the handler when disposed.</returns>
    IDisposable Register<TMessage>(AsyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter);
    /// <summary>
    /// Registers a reactive handler for messages of the given runtime type.
    /// </summary>
    /// <param name="tMessage">The message type to handle.</param>
    /// <param name="action">The handler to invoke.</param>
    /// <returns>A handle that unregisters the handler when disposed.</returns>
    IDisposable Register(Type tMessage, AsyncDelivery action);
    /// <summary>
    /// Registers a reactive, filtered handler for messages of the given runtime type.
    /// </summary>
    /// <param name="tMessage">The message type to handle.</param>
    /// <param name="action">The handler to invoke.</param>
    /// <param name="filter">Predicate selecting which deliveries the handler receives.</param>
    /// <returns>A handle that unregisters the handler when disposed.</returns>
    IDisposable Register(Type tMessage, AsyncDelivery action, DeliveryFilter filter);
    /// <summary>
    /// Registers a synchronous handler for messages of the given runtime type.
    /// </summary>
    /// <param name="tMessage">The message type to handle.</param>
    /// <param name="action">The handler to invoke.</param>
    /// <returns>A handle that unregisters the handler when disposed.</returns>
    IDisposable Register(Type tMessage, SyncDelivery action);
    /// <summary>
    /// Registers a synchronous, filtered handler for messages of the given runtime type.
    /// </summary>
    /// <param name="tMessage">The message type to handle.</param>
    /// <param name="action">The handler to invoke.</param>
    /// <param name="filter">Predicate selecting which deliveries the handler receives.</param>
    /// <returns>A handle that unregisters the handler when disposed.</returns>
    IDisposable Register(Type tMessage, SyncDelivery action, DeliveryFilter filter);
    /// <summary>
    /// Registers a reactive handler that also applies to subtypes of <typeparamref name="TMessage"/>.
    /// </summary>
    /// <typeparam name="TMessage">The base message type to handle (including derived types).</typeparam>
    /// <param name="action">The handler to invoke.</param>
    /// <param name="filter">Optional predicate selecting which deliveries the handler receives.</param>
    /// <returns>A handle that unregisters the handler when disposed.</returns>
    IDisposable RegisterInherited<TMessage>(
        AsyncDelivery<TMessage> action,
        DeliveryFilter<TMessage>? filter = null
    );
    /// <summary>
    /// Registers a synchronous handler that also applies to subtypes of <typeparamref name="TMessage"/>.
    /// </summary>
    /// <typeparam name="TMessage">The base message type to handle (including derived types).</typeparam>
    /// <param name="action">The handler to invoke.</param>
    /// <param name="filter">Optional predicate selecting which deliveries the handler receives.</param>
    /// <returns>A handle that unregisters the handler when disposed.</returns>
    IDisposable RegisterInherited<TMessage>(
        SyncDelivery<TMessage> action,
        DeliveryFilter<TMessage>? filter = null
    );
    /// <summary>
    /// Registers a synchronous catch-all handler for any message delivery.
    /// </summary>
    /// <param name="delivery">The handler to invoke.</param>
    /// <returns>A handle that unregisters the handler when disposed.</returns>
    IDisposable Register(SyncDelivery delivery);
    /// <summary>
    /// Registers a reactive catch-all handler for any message delivery.
    /// </summary>
    /// <param name="delivery">The handler to invoke.</param>
    /// <returns>A handle that unregisters the handler when disposed.</returns>
    IDisposable Register(AsyncDelivery delivery);
}
