namespace vamos.Models;

/// <summary>
/// One step of the survey, containing one or more questions.
/// </summary>
public sealed class SurveyStep
{
    public int Number { get; init; }

    public string Title { get; init; } = string.Empty;

    public IReadOnlyList<Question> Questions { get; init; } = Array.Empty<Question>();
}
