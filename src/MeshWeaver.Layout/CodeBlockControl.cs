using MeshWeaver.ShortGuid;

namespace MeshWeaver.Layout;

public record CodeBlockControl(object Code, object Language) : UiControl<CodeBlockControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public object ExecutionArea { get; init; }
    public CodeBlockControl Execute(object executionArea) => 
        this with { ExecutionArea = executionArea };

    public object CodeHidden { get; init; }

    public CodeBlockControl HideCode(object hideCode)
        => this with { CodeHidden = hideCode };

    public object OutputHidden { get; init; }
    public CodeBlockControl HideOutput(object hideCode)
        => this with { OutputHidden = hideCode };

    public object HeaderShown { get; init; }

    public CodeBlockControl ShowHeader(object headerShown)
        => this with { HeaderShown = headerShown };

    public object Html { get; init; }
    public CodeBlockControl WithHtml(object codeHtml)
        => this with { Html = codeHtml };

    public object MarkdownArguments { get; init; }
    public CodeBlockControl WithMarkdownArguments(object markdownArguments)
        => this with { MarkdownArguments = markdownArguments };


    public const string HideCodeParameter = "hide-code";
    public const string HideOutputParameter = "hide-output";
    public const string ShowHeaderParameter = "show-header";
    public const string ExecuteParameter = "execute";

    public CodeBlockControl WithArguments(string arguments)
    {
        return ParseArguments(arguments)
            .Aggregate(
                this with { MarkdownArguments = arguments },
                (c, p)
                    => c.WithValue(p)
            );
    }

    private CodeBlockControl WithValue(KeyValuePair<string, string> kvp)
        => kvp.Key switch
        {
            HideCodeParameter => HideCode(kvp.Value ?? "true"),
            HideOutputParameter => HideOutput(kvp.Value ?? "true"),
            ShowHeaderParameter => ShowHeader(kvp.Value ?? "true"),
            ExecuteParameter => Execute(kvp.Value ?? $"'{Guid.NewGuid().AsString()}'"),
            _ => this
        };

    private static IEnumerable<KeyValuePair<string, string>> ParseArguments(string arguments)
    {
        var linear = (arguments ?? string.Empty).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < linear.Length; i++)
        {
            var arg = linear[i];
            if (arg.StartsWith("--"))
            {
                var key = arg.Substring(2).ToLowerInvariant();
                string value = null;
                if (i + 1 < linear.Length)
                    if (linear[i + 1].StartsWith("--"))
                    {
                        yield return new(key, null);
                        continue;
                    }
                    else
                        value = linear[++i].ToLowerInvariant();
                yield return new(key, value);
            }
        }

    }

}
