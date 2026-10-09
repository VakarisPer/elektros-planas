namespace vamos.Models;

/// <summary>
/// The user's answer to a single <see cref="Question"/>.
/// </summary>
public sealed class Answer
{
    public int QuestionId { get; set; }

    /// <summary>Selected option labels. May contain <see cref="Question.CustomOption"/>.</summary>
    public List<string> SelectedOptions { get; set; } = new();

    /// <summary>Free-text answer typed by the user.</summary>
    public string? CustomText { get; set; }

    /// <summary>True when the answer was filled automatically from an uploaded file.</summary>
    public bool IsAuto { get; set; }
}
