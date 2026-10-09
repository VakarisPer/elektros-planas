namespace vamos.Models;

/// <summary>
/// Serializable snapshot of the survey, persisted to localStorage.
/// </summary>
public sealed class SurveySnapshot
{
    public List<Answer> Answers { get; set; } = new();

    public int CurrentStep { get; set; } = 1;
}
