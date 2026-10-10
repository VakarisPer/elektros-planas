namespace vamos.Models;

/// <summary>
/// Serializable snapshot of the survey, persisted to localStorage.
/// </summary>
public sealed class SurveySnapshot
{
    public List<Answer> Answers { get; set; } = new();

    public int CurrentQuestionId { get; set; } = 1;

    public ConsumptionAnalysis? Analysis { get; set; }

    public string? LastSavedPath { get; set; }

    public Recommendation? Recommendation { get; set; }

    public List<ChatEntry> ChatLog { get; set; } = new();

    public List<int> AskedQuestionIds { get; set; } = new();

    public int? PendingQuestionId { get; set; }

    public EsoConnectionInfo? EsoConnection { get; set; }
}
