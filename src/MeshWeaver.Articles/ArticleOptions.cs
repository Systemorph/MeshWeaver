﻿namespace MeshWeaver.Articles;

public record ArticleOptions
{
    public bool ContentIncluded { get; init; } = true;
    public ArticleOptions IncludeContent(bool include = true) => this with { ContentIncluded = include };
    public bool PrerenderIncluded { get; init; } = true;
    public ArticleOptions IncludePrerendered(bool include = true) => this with { PrerenderIncluded = include };
}
