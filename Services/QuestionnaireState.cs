using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;
using vamos.Data;
using vamos.Models;

namespace vamos.Services;

/// <summary>
/// Holds the answers of the current survey session, walks the question tree and
/// persists everything to localStorage. Registered as scoped (one per circuit).
/// </summary>
public sealed class QuestionnaireState
{
    private const string StorageKey = "elektros-planai.survey.v7";

    private static readonly CultureInfo Lt = CultureInfo.GetCultureInfo("lt-LT");

    private readonly ProtectedLocalStorage _storage;
    private readonly SubmissionWriter _writer;
    private readonly Dictionary<int, Answer> _answers = new();
    private bool _loaded;

    public QuestionnaireState(ProtectedLocalStorage storage, SubmissionWriter writer)
    {
        _storage = storage;
        _writer = writer;
    }

    public int CurrentQuestionId { get; set; } = Questionnaire.FirstQuestionId;

    public ConsumptionAnalysis? Analysis { get; set; }

    public string? LastSavedPath { get; set; }

    /// <summary>Final advice (AI or rule-based); null until the advisor finishes.</summary>
    public Recommendation? Recommendation { get; set; }

    /// <summary>Assistant conversation shown on /assistant.</summary>
    public List<ChatEntry> ChatLog { get; private set; } = new();

    /// <summary>Questions the assistant has asked, in order.</summary>
    public List<int> AskedQuestionIds { get; private set; } = new();

    /// <summary>Question currently waiting for the user's answer in the assistant.</summary>
    public int? PendingQuestionId { get; set; }

    /// <summary>Last ESO grid connection check (object number, installed / permitted kW).</summary>
    public EsoConnectionInfo? EsoConnection { get; set; }

    /// <summary>
    /// Stores an ESO check and fills the answers it settles: the connectable power answers the
    /// capacity question; an installed plant means own solar and its exact size answers the plant-size question.
    /// </summary>
    public void ApplyEsoConnection(EsoConnectionInfo info)
    {
        EsoConnection = info;
        if (!info.Success)
        {
            return;
        }

        if (GetAnswer(Questionnaire.CapacityId).SelectedOptions.Count == 0 || GetAnswer(Questionnaire.CapacityId).IsAuto)
        {
            SetAutoAnswer(Questionnaire.CapacityId,
                info.AvailableCapacityKw is < 5 ? Questionnaire.CapacityLow : Questionnaire.CapacityOk);
        }

        if (info.InstalledGenerationKw is not > 0 || info.InstalledGenerationKw is not { } kw)
        {
            return;
        }

        if (GetAnswer(8).SelectedOptions.Count == 0 || GetAnswer(8).IsAuto)
        {
            SetAutoAnswer(8, Questionnaire.OwnSolar);
        }

        SetAutoAnswer(26, kw switch
        {
            < 5 => "Iki 5 kW",
            <= 10 => "5–10 kW",
            _ => "Virš 10 kW"
        });
    }

    // ── Answer access ───────────────────────────────────────────────────

    public Answer GetAnswer(int questionId)
    {
        if (!_answers.TryGetValue(questionId, out var answer))
        {
            answer = new Answer { QuestionId = questionId };
            _answers[questionId] = answer;
        }

        return answer;
    }

    public void SetSingle(int questionId, string option, bool isAuto = false)
    {
        var answer = GetAnswer(questionId);
        answer.SelectedOptions.Clear();
        answer.SelectedOptions.Add(option);
        answer.IsAuto = isAuto;
    }

    public void ClearSelection(int questionId)
    {
        var answer = GetAnswer(questionId);
        answer.SelectedOptions.Clear();
        answer.IsAuto = false;
    }

    public void ToggleMulti(int questionId, string option, bool isChecked, string? exclusiveOption)
    {
        var answer = GetAnswer(questionId);
        answer.IsAuto = false;

        if (!isChecked)
        {
            answer.SelectedOptions.Remove(option);
            return;
        }

        if (exclusiveOption is not null)
        {
            if (option == exclusiveOption)
            {
                answer.SelectedOptions.Clear();
            }
            else
            {
                answer.SelectedOptions.Remove(exclusiveOption);
            }
        }

        if (!answer.SelectedOptions.Contains(option))
        {
            answer.SelectedOptions.Add(option);
        }
    }

    public void SetCustomText(int questionId, string? text)
    {
        var answer = GetAnswer(questionId);
        answer.CustomText = text;
        answer.IsAuto = false;
    }

    /// <summary>Fills a single-choice answer automatically (from the uploaded file).</summary>
    public void SetAutoAnswer(int questionId, string option) => SetSingle(questionId, option, isAuto: true);

    public bool IsQuestionAnswered(Question question)
    {
        var answer = GetAnswer(question.Id);
        var hasConcreteOption = answer.SelectedOptions.Any(o => o != Question.CustomOption);
        return hasConcreteOption || !string.IsNullOrWhiteSpace(answer.CustomText);
    }

    // ── Tree walking ────────────────────────────────────────────────────

    public bool IsVisible(Question question) => question.ShowWhen.All(ConditionHolds);

    /// <summary>Notes of a question whose conditions currently hold.</summary>
    public IReadOnlyList<QuestionNote> NotesFor(Question question) =>
        question.Notes.Where(n => n.ShowWhen.All(ConditionHolds)).ToList();

    private bool ConditionHolds(ShowCondition condition)
    {
        var answer = GetAnswer(condition.QuestionId);
        var answered = answer.SelectedOptions.Count > 0 || !string.IsNullOrWhiteSpace(answer.CustomText);

        // A question whose dependency is not answered yet is not shown.
        if (!answered)
        {
            return false;
        }

        if (condition.HasAny is { Count: > 0 } any && !any.Any(answer.SelectedOptions.Contains))
        {
            return false;
        }

        if (condition.HasNone is { Count: > 0 } none && none.Any(answer.SelectedOptions.Contains))
        {
            return false;
        }

        return true;
    }

    public IReadOnlyList<Question> VisibleQuestions() =>
        Questionnaire.Questions.Where(IsVisible).ToList();

    public int VisibleCount => VisibleQuestions().Count;

    public int PositionOf(int questionId)
    {
        var index = VisibleQuestions().ToList().FindIndex(q => q.Id == questionId);
        return index < 0 ? 0 : index + 1;
    }

    public int? PreviousVisibleId(int questionId)
    {
        Question? previous = null;
        foreach (var question in Questionnaire.Questions)
        {
            if (question.Id == questionId)
            {
                break;
            }

            if (IsVisible(question))
            {
                previous = question;
            }
        }

        return previous?.Id;
    }

    public int? NextVisibleId(int questionId)
    {
        var found = false;
        foreach (var question in Questionnaire.Questions)
        {
            if (found)
            {
                if (IsVisible(question))
                {
                    return question.Id;
                }
            }
            else if (question.Id == questionId)
            {
                found = true;
            }
        }

        return null;
    }

    public int? FirstVisibleUnansweredId() =>
        Questionnaire.Questions.Where(IsVisible).Where(q => !IsQuestionAnswered(q))
            .Select(q => (int?)q.Id).FirstOrDefault();

    /// <summary>Nearest visible question at or before the given one; used when it became hidden.</summary>
    public int NearestVisibleId(int questionId)
    {
        int? last = null;
        foreach (var question in Questionnaire.Questions)
        {
            if (IsVisible(question))
            {
                last = question.Id;
            }

            if (question.Id == questionId)
            {
                break;
            }
        }

        return last ?? VisibleQuestions().Select(q => q.Id).FirstOrDefault();
    }

    public bool IsComplete => VisibleQuestions().All(IsQuestionAnswered);

    /// <summary>Visible questions that have an answer, in funnel order.</summary>
    public IReadOnlyList<Question> AnsweredQuestions() =>
        Questionnaire.Questions.Where(q => IsVisible(q) && IsQuestionAnswered(q)).ToList();

    // ── Persistence ─────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        try
        {
            var result = await _storage.GetAsync<SurveySnapshot>(StorageKey);
            if (result.Success && result.Value is { } snapshot)
            {
                _answers.Clear();
                foreach (var answer in snapshot.Answers)
                {
                    _answers[answer.QuestionId] = answer;
                }

                CurrentQuestionId = snapshot.CurrentQuestionId;
                Analysis = snapshot.Analysis;
                LastSavedPath = snapshot.LastSavedPath;
                Recommendation = snapshot.Recommendation;
                ChatLog = snapshot.ChatLog;
                AskedQuestionIds = snapshot.AskedQuestionIds;
                PendingQuestionId = snapshot.PendingQuestionId;
                EsoConnection = snapshot.EsoConnection;
            }
        }
        catch (JSException) { }
        catch (InvalidOperationException) { }
    }

    public async Task SaveAsync()
    {
        var snapshot = new SurveySnapshot
        {
            Answers = _answers.Values.ToList(),
            CurrentQuestionId = CurrentQuestionId,
            Analysis = Analysis,
            LastSavedPath = LastSavedPath,
            Recommendation = Recommendation,
            ChatLog = ChatLog,
            AskedQuestionIds = AskedQuestionIds,
            PendingQuestionId = PendingQuestionId,
            EsoConnection = EsoConnection
        };

        try
        {
            await _storage.SetAsync(StorageKey, snapshot);
        }
        catch (JSException) { }
        catch (InvalidOperationException) { }
    }

    public async Task ResetAsync()
    {
        _answers.Clear();
        CurrentQuestionId = Questionnaire.FirstQuestionId;
        Analysis = null;
        LastSavedPath = null;
        Recommendation = null;
        ChatLog = new();
        AskedQuestionIds = new();
        PendingQuestionId = null;
        EsoConnection = null;

        try
        {
            await _storage.DeleteAsync(StorageKey);
        }
        catch (JSException) { }
        catch (InvalidOperationException) { }
    }

    // ── Submission (.txt) ───────────────────────────────────────────────

    public async Task<string> SubmitAsync()
    {
        var path = await _writer.WriteAsync(BuildTextReport());
        LastSavedPath = path;
        await SaveAsync();
        return path;
    }

    public string BuildTextReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Elektros plano apklausos atsakymai");
        sb.AppendLine($"Data: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        if (Analysis is { Success: true } a)
        {
            sb.AppendLine();
            sb.AppendLine("Įkelto failo analizė:");
            sb.AppendLine($"  Failas: {a.FileName}");
            sb.AppendLine($"  Įrašų: {a.Readings}, mėnesių: {a.Months}");
            if (a.MonthlyAverageKwh is { } avg)
            {
                sb.AppendLine($"  Vidutiniškai: {avg.ToString("0.0", Lt)} kWh/mėn.");
            }
            if (a.NightShare is { } night)
            {
                sb.AppendLine($"  Naktinis suvartojimas: {(night * 100).ToString("0", Lt)}%");
            }
        }

        string? section = null;
        if (Recommendation is { } r)
        {
            sb.AppendLine();
            sb.AppendLine($"Rekomendacija: {r.PlanLabel ?? Recommendation.PlanTypeLabel(r.PlanType) + " (" + r.Supplier + ")"}, pasitikėjimas: {r.Confidence}");
            sb.AppendLine(r.SummaryMarkdown);
        }

        foreach (var question in AnsweredQuestions())
        {
            if (question.Section != section)
            {
                section = question.Section;
                sb.AppendLine();
                sb.AppendLine($"[{section}]");
            }

            var answer = GetAnswer(question.Id);
            var marker = answer.IsAuto ? "   (automatinis)" : string.Empty;
            sb.AppendLine($"{question.Id}. {question.Text}");
            sb.AppendLine($"   Atsakymas: {FormatAnswer(answer)}{marker}");
        }

        return sb.ToString();
    }

    public static string FormatAnswer(Answer answer)
    {
        var parts = answer.SelectedOptions
            .Select(o => o == Question.CustomOption ? answer.CustomText : o)
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o!)
            .ToList();

        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(answer.CustomText))
        {
            parts.Add(answer.CustomText!);
        }

        return parts.Count == 0 ? "—" : string.Join(", ", parts);
    }
}
