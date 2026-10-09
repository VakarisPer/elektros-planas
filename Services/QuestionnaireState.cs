using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;
using vamos.Data;
using vamos.Models;

namespace vamos.Services;

/// <summary>
/// Holds the answers of the current survey session and persists them to localStorage.
/// Registered as scoped, so one instance per interactive circuit.
/// </summary>
public sealed class QuestionnaireState
{
    private const string StorageKey = "elektros-planai.survey.v1";

    private readonly ProtectedLocalStorage _storage;
    private readonly Dictionary<int, Answer> _answers = new();
    private bool _loaded;

    public QuestionnaireState(ProtectedLocalStorage storage) => _storage = storage;

    public int CurrentStep { get; set; } = 1;

    public Answer GetAnswer(int questionId)
    {
        if (!_answers.TryGetValue(questionId, out var answer))
        {
            answer = new Answer { QuestionId = questionId };
            _answers[questionId] = answer;
        }

        return answer;
    }

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

                CurrentStep = snapshot.CurrentStep;
            }
        }
        catch (JSException)
        {
            // JS interop is not available yet (e.g. during prerendering).
        }
        catch (InvalidOperationException)
        {
            // ProtectedBrowserStorage cannot be used in this rendering mode.
        }
    }

    public async Task SaveAsync()
    {
        var snapshot = new SurveySnapshot
        {
            Answers = _answers.Values.ToList(),
            CurrentStep = CurrentStep
        };

        try
        {
            await _storage.SetAsync(StorageKey, snapshot);
        }
        catch (JSException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public async Task ResetAsync()
    {
        _answers.Clear();
        CurrentStep = 1;

        try
        {
            await _storage.DeleteAsync(StorageKey);
        }
        catch (JSException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void SetSingle(int questionId, string option)
    {
        var answer = GetAnswer(questionId);
        answer.SelectedOptions.Clear();
        answer.SelectedOptions.Add(option);
    }

    public void ClearSelection(int questionId) => GetAnswer(questionId).SelectedOptions.Clear();

    public void ToggleMulti(int questionId, string option, bool isChecked, string? exclusiveOption)
    {
        var answer = GetAnswer(questionId);

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

    public void SetCustomText(int questionId, string? text) =>
        GetAnswer(questionId).CustomText = text;

    public bool IsQuestionAnswered(Question question)
    {
        var answer = GetAnswer(question.Id);
        var hasConcreteOption = answer.SelectedOptions.Any(o => o != Question.CustomOption);
        return hasConcreteOption || !string.IsNullOrWhiteSpace(answer.CustomText);
    }

    public bool IsStepComplete(int stepNumber)
    {
        var step = Questionnaire.GetStep(stepNumber);
        return step is not null && step.Questions.All(IsQuestionAnswered);
    }

    public bool IsComplete => Questionnaire.Steps.All(s => s.Questions.All(IsQuestionAnswered));
}
