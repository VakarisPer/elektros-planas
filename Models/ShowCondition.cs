namespace vamos.Models;

/// <summary>
/// Decides whether a question is shown, based on the answer of an earlier question.
/// This is the "tree questioning" building block. All conditions of a question
/// are combined with AND.
/// </summary>
public sealed class ShowCondition
{
    /// <summary>Id of the question this condition depends on.</summary>
    public int QuestionId { get; init; }

    /// <summary>Show only if the referenced answer contains at least one of these options.</summary>
    public IReadOnlyList<string>? HasAny { get; init; }

    /// <summary>Show only if the referenced answer contains none of these options.</summary>
    public IReadOnlyList<string>? HasNone { get; init; }
}
