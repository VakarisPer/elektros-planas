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

    /// <summary>Grouping label shown as the page heading (e.g. "Didieji vartotojai").</summary>
    public string Section { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Why this question matters for the recommendation. Shown to the AI advisor so it can
    /// decide which question carries the most information next.
    /// </summary>
    public string Purpose { get; init; } = string.Empty;

    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();

    /// <summary>When true the user may select several options (checkboxes).</summary>
    public bool IsMultiSelect { get; init; }

    /// <summary>For multi-select questions: option that cannot be combined with others.</summary>
    public string? ExclusiveOption { get; init; }

    /// <summary>When true the "other (type your own)" choice is offered.</summary>
    public bool AllowCustom { get; init; } = true;

    /// <summary>
    /// Conditions that decide whether this question is relevant. All conditions are
    /// combined with AND. Empty means the question is always shown (tree root).
    /// </summary>
    public IReadOnlyList<ShowCondition> ShowWhen { get; init; } = Array.Empty<ShowCondition>();
}
