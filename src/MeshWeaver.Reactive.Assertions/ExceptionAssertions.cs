using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MeshWeaver.Reactive.Assertions;

/// <summary>Assertions over a thrown exception (the result of <c>Throw&lt;T&gt;()</c> / <c>await ThrowAsync&lt;T&gt;()</c>).</summary>
public class ExceptionAssertions<TException>(TException exception) where TException : Exception
{
    /// <summary>The caught exception.</summary>
    public TException Which => exception;
    /// <summary>Alias for <see cref="Which"/>.</summary>
    public TException Subject => exception;
    /// <summary>The caught exception (FA-compatible accessor).</summary>
    public TException And => exception;

    public AndConstraint<ExceptionAssertions<TException>> WithMessage(string wildcardPattern, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(WildCard.IsMatch(exception.Message, wildcardPattern),
            () => $"Expected exception message to match {Az.Fmt(wildcardPattern)}{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(exception.Message)}.");
        return new(this);
    }

    public AndWhichConstraint<ExceptionAssertions<TException>, TInner> WithInnerException<TInner>(string because = "", params object[] becauseArgs)
        where TInner : Exception
    {
        Az.Ensure(exception.InnerException is TInner,
            () => $"Expected inner exception of type {typeof(TInner).Name}{Az.Reason(because, becauseArgs)}, but found {exception.InnerException?.GetType().Name ?? "<null>"}.");
        return new(this, exception.InnerException is TInner inner ? inner : default!);
    }
}

/// <summary>Assertions for a synchronous <see cref="Action"/> — whether it throws.</summary>
public class ActionAssertions(Action subject) : ObjectAssertions<Action, ActionAssertions>(subject)
{
    public ExceptionAssertions<TException> Throw<TException>(string because = "", params object[] becauseArgs) where TException : Exception
    {
        Exception? caught = Capture(Subject);
        Az.Ensure(caught is TException, () => ThrowMessage<TException>(caught, because, becauseArgs));
        return new ExceptionAssertions<TException>((TException)caught!);
    }

    public AndConstraint<ActionAssertions> NotThrow(string because = "", params object[] becauseArgs)
    {
        Exception? caught = Capture(Subject);
        Az.Ensure(caught is null, () => $"Did not expect an exception{Az.Reason(because, becauseArgs)}, but found {caught?.GetType().Name}: {caught?.Message}.");
        return new(this);
    }

    internal static Exception? Capture(Action action)
    {
        try { action(); return null; }
        catch (Exception ex) { return ex; }
    }

    internal static string ThrowMessage<TException>(Exception? caught, string because, object[] becauseArgs)
        => $"Expected a {typeof(TException).Name}{Az.Reason(because, becauseArgs)}, but " +
           (caught is null ? "no exception was thrown." : $"found {caught.GetType().Name}: {caught.Message}.");
}

/// <summary>Assertions for an async <see cref="Func{Task}"/> — whether it throws.</summary>
public class AsyncFunctionAssertions(Func<Task> subject) : ObjectAssertions<Func<Task>, AsyncFunctionAssertions>(subject)
{
    /// <summary>
    /// Returns an awaitable continuation: <c>await act.Should().ThrowAsync&lt;T&gt;()</c> or
    /// <c>await act.Should().ThrowAsync&lt;T&gt;().WithMessage("*…*")</c>.
    /// </summary>
    public ThrowAsyncContinuation<TException> ThrowAsync<TException>(string because = "", params object[] becauseArgs) where TException : Exception
        => new(Subject, because, becauseArgs);

    public async Task NotThrowAsync(string because = "", params object[] becauseArgs)
    {
        Exception? caught = null;
        try { await Subject(); } catch (Exception ex) { caught = ex; }
        Az.Ensure(caught is null, () => $"Did not expect an exception{Az.Reason(because, becauseArgs)}, but found {caught?.GetType().Name}: {caught?.Message}.");
    }
}

/// <summary>
/// Awaitable result of <c>ThrowAsync&lt;T&gt;()</c>. Supports both <c>await ThrowAsync&lt;T&gt;()</c> and
/// <c>await ThrowAsync&lt;T&gt;().WithMessage("*…*")</c> — the assertion runs when awaited.
/// </summary>
public sealed class ThrowAsyncContinuation<TException> where TException : Exception
{
    private readonly Func<Task> _action;
    private readonly string _because;
    private readonly object[] _becauseArgs;
    private string? _messagePattern;
    private string _messageBecause = "";
    private object[] _messageArgs = [];

    internal ThrowAsyncContinuation(Func<Task> action, string because, object[] becauseArgs)
    {
        _action = action;
        _because = because;
        _becauseArgs = becauseArgs;
    }

    public ThrowAsyncContinuation<TException> WithMessage(string wildcardPattern, string because = "", params object[] becauseArgs)
    {
        _messagePattern = wildcardPattern;
        _messageBecause = because;
        _messageArgs = becauseArgs;
        return this;
    }

    public TaskAwaiter<ExceptionAssertions<TException>> GetAwaiter() => RunAsync().GetAwaiter();

    private async Task<ExceptionAssertions<TException>> RunAsync()
    {
        Exception? caught = null;
        try { await _action(); } catch (Exception ex) { caught = ex; }
        Az.Ensure(caught is TException, () => ActionAssertions.ThrowMessage<TException>(caught, _because, _becauseArgs));
        var typed = (TException)caught!;
        if (_messagePattern != null)
            Az.Ensure(WildCard.IsMatch(typed.Message, _messagePattern),
                () => $"Expected exception message to match {Az.Fmt(_messagePattern)}{Az.Reason(_messageBecause, _messageArgs)}, but found {Az.Fmt(typed.Message)}.");
        return new ExceptionAssertions<TException>(typed);
    }
}

/// <summary>FA-style wildcard matching (<c>*</c> = any run, <c>?</c> = any single char), used by message asserts.</summary>
internal static class WildCard
{
    public static bool IsMatch(string? input, string wildcardPattern)
    {
        if (input is null) return false;
        var rx = "^" + Regex.Escape(wildcardPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(input, rx, RegexOptions.Singleline);
    }
}
