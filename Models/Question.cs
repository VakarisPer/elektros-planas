namespace vamos.Models;

/// <summary>
/// A single survey question with its selectable options.
/// </summary>
public sealed class Question
{
    /// <summary>
    /// Marker stored in <see cref="Answer.SelectedOptions"/> when the user picks the
    /// free-text "other" option.
    /// </summary>
    public const string CustomOption = "__custom__";

    public int Id { get; init; }

    public int Step { get; init; }

    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();

    /// <summary>When true the user may select several options (checkboxes).</summary>
    public bool IsMultiSelect { get; init; }

    /// <summary>For multi-select questions: option that cannot be combined with others.</summary>
    public string? ExclusiveOption { get; init; }

    /// <summary>When true the "other (type your own)" choice is offered.</summary>
    public bool AllowCustom { get; init; } = true;
}
