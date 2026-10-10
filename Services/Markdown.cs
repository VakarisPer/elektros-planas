using Markdig;
using Microsoft.AspNetCore.Components;

namespace vamos.Services;

/// <summary>Renders model-written Markdown safely (raw HTML is disabled).</summary>
public static class Markdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .Build();

    public static MarkupString ToHtml(string? markdown) =>
        new(Markdig.Markdown.ToHtml(markdown ?? string.Empty, Pipeline));
}
