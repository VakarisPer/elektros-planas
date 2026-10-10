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

    /// <summary>
    /// Free-text question: a text area plus the options as "skip" buttons (no "other" choice).
    /// The answer is stored in <see cref="Answer.CustomText"/>.
    /// </summary>
    public bool IsFreeText { get; init; }

    /// <summary>When true the "other (type your own)" choice is offered.</summary>
    public bool AllowCustom { get; init; } = true;

    /// <summary>
    /// Not part of the project's question scheme (e.g. heating, attitude to bills): asked by the classic
    /// wizard, optional for the AI advisor.
    /// </summary>
    public bool Supplementary { get; init; }

    /// <summary>Hints shown above the question when their conditions hold (e.g. "a smart meter is needed").</summary>
    public IReadOnlyList<QuestionNote> Notes { get; init; } = Array.Empty<QuestionNote>();

    /// <summary>Shows the ESO grid connection check (object number) with the question.</summary>
    public bool ShowEsoCheck { get; init; }

    /// <summary>
    /// Conditions that decide whether this question is relevant. All conditions are
    /// combined with AND. Empty means the question is always shown (tree root).
    /// </summary>
    public IReadOnlyList<ShowCondition> ShowWhen { get; init; } = Array.Empty<ShowCondition>();
}

/// <summary>A hint shown with a question, optionally only when its conditions hold.</summary>
public sealed class QuestionNote
{
    public string Text { get; init; } = string.Empty;

    /// <summary>Optional link shown after the text.</summary>
    public string? Url { get; init; }

    public string? LinkText { get; init; }

    /// <summary>All must hold (AND); empty = always shown.</summary>
    public IReadOnlyList<ShowCondition> ShowWhen { get; init; } = Array.Empty<ShowCondition>();
}
