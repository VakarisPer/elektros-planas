using System.ComponentModel;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using vamos.Data;
using vamos.Models;

namespace vamos.Services;

/// <summary>
/// The AI advisor: a Microsoft Agent Framework <see cref="ChatClientAgent"/> that picks the next
/// most informative question from <see cref="Questionnaire"/> and, once confident, writes the
/// recommendation based on <see cref="PlanCalculator"/> numbers. One instance per circuit.
/// Without an API key (or when the model fails) it falls back to the rule-based tree.
/// </summary>
public sealed class ElectricityAdvisorAgent
{
    private const string NudgeMessage =
        "[SISTEMA] Ėjimas baigtas be veiksmo. Iškviesk ask_question su vienu iš galimų klausimų " +
        "arba estimate_plans ir finish.";

    private readonly QuestionnaireState _state;
    private readonly PlanCalculator _calculator;
    private readonly SolarAdvisor _solar;
    private readonly EsoConnectionService _eso;
    private readonly OpenRouterOptions _options;
    private readonly ILogger<ElectricityAdvisorAgent> _logger;
    private readonly ChatClientAgent? _agent;

    private AgentSession? _session;
    private bool _askedThisTurn;
    private bool _finishedThisTurn;
    private bool _forceFinish;
    private bool _freeTextTurn;

    public ElectricityAdvisorAgent(
        QuestionnaireState state,
        PlanCalculator calculator,
        SolarAdvisor solar,
        EsoConnectionService eso,
        IOptions<OpenRouterOptions> options,
        AdvisorPrompt prompt,
        ILogger<ElectricityAdvisorAgent> logger,
        IChatClient? chatClient = null)
    {
        _state = state;
        _calculator = calculator;
        _solar = solar;
        _eso = eso;
        _options = options.Value;
        _logger = logger;

        if (chatClient is not null && _options.IsConfigured)
        {
            var tools = new List<AITool>
            {
                AIFunctionFactory.Create(GetProfile, "get_profile"),
                AIFunctionFactory.Create(AskQuestion, "ask_question"),
                AIFunctionFactory.Create(RecordAnswer, "record_answer"),
                AIFunctionFactory.Create(EstimatePlans, "estimate_plans"),
                AIFunctionFactory.Create(SimulateSolar, "simulate_solar"),
                AIFunctionFactory.Create(CheckGridConnectionAsync, "check_grid_connection"),
                AIFunctionFactory.Create(Finish, "finish")
            };

            _agent = new ChatClientAgent(
                chatClient,
                instructions: prompt.Render(_options.MaxQuestions),
                name: "ElektrosPlanoPatarejas",
                description: "Padeda išsirinkti elektros planą",
                tools: tools);
        }
    }

    public bool IsAiEnabled => _agent is not null;

    public bool IsFinished => _state.Recommendation is not null;

    // ── Conversation entry points (called by Assistant.razor) ───────────

    /// <summary>Starts (or resumes) the conversation.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        // The pending question may have been answered automatically by an uploaded file.
        if (_state.PendingQuestionId is { } pendingId && !IsFinished &&
            Questionnaire.GetQuestion(pendingId) is { } pending && _state.IsQuestionAnswered(pending))
        {
            _state.PendingQuestionId = null;
            return RunTurnAsync("Vartotojas įkėlė ESO suvartojimo failą, dalis atsakymų užpildyta automatiškai. Tęsk.", ct);
        }

        if (_state.PendingQuestionId is not null || IsFinished)
        {
            return Task.CompletedTask;
        }

        var known = _state.AnsweredQuestions().Count;
        var opening = known > 0
            ? $"Vartotojas grįžo; jau žinomi {known} atsakymai. Tęsk nuo ten, kur sustota."
            : "Pasisveikinimas ir pranešimas, kad pabaigoje vartotojas galės laisvai papasakoti apie save, jau parodyti. " +
              "Iškart užduok pirmą svarbiausią klausimą.";

        if (known == 0 && _state.ChatLog.Count == 0)
        {
            _state.ChatLog.Add(new ChatEntry
            {
                Text = "Sveiki! Užduosiu kelis klausimus apie jūsų būstą ir įpročius, o tada palyginsiu elektros planus. " +
                       Questionnaire.StartNotice
            });
        }

        return RunTurnAsync(opening, ct);
    }

    /// <summary>The user picked options for the pending question in the UI.</summary>
    public Task AnswerPendingAsync(CancellationToken ct = default)
    {
        if (_state.PendingQuestionId is not { } id || Questionnaire.GetQuestion(id) is not { } question)
        {
            return Task.CompletedTask;
        }

        var answerText = QuestionnaireState.FormatAnswer(_state.GetAnswer(id));
        _state.ChatLog.Add(new ChatEntry { FromUser = true, Text = answerText });
        _state.PendingQuestionId = null;

        return RunTurnAsync($"Atsakymas į Q{id} („{question.Text}“): {answerText}", ct);
    }

    /// <summary>The user typed free text in the chat box.</summary>
    public Task SendFreeTextAsync(string text, CancellationToken ct = default)
    {
        _state.ChatLog.Add(new ChatEntry { FromUser = true, Text = text });

        var pending = _state.PendingQuestionId is { } id
            ? $" (tuo metu buvo užduotas Q{id} – jei tekstas į jį atsako, užfiksuok record_answer)"
            : string.Empty;

        _freeTextTurn = true;
        return RunTurnAsync($"Vartotojas parašė{pending}: {text}", ct);
    }

    /// <summary>The user asks to stop and see the result now.</summary>
    public Task FinishNowAsync(CancellationToken ct = default)
    {
        _state.PendingQuestionId = null;
        return RunTurnAsync("Vartotojas nori rezultato DABAR. Iškviesk estimate_plans ir finish su tuo, ką žinai.", ct, forceFinish: true);
    }

    // ── Turn execution ──────────────────────────────────────────────────

    private async Task RunTurnAsync(string message, CancellationToken ct, bool forceFinish = false)
    {
        _askedThisTurn = false;
        _finishedThisTurn = false;
        _forceFinish = forceFinish;

        if (_agent is null)
        {
            RuleBasedTurn(forceFinish);
            _freeTextTurn = false;
            await _state.SaveAsync();
            return;
        }

        try
        {
            _session ??= await _agent.CreateSessionAsync(ct);

            var response = await _agent.RunAsync($"{message}\n\n{BuildStateSummary()}", _session, cancellationToken: ct);
            AddAssistantText(response.Text);

            if (!_askedThisTurn && !_finishedThisTurn && _state.PendingQuestionId is null)
            {
                response = await _agent.RunAsync($"{NudgeMessage}\n\n{BuildStateSummary()}", _session, cancellationToken: ct);
                AddAssistantText(response.Text);
            }

            if (!_askedThisTurn && !_finishedThisTurn && (_state.PendingQuestionId is null || forceFinish))
            {
                RuleBasedTurn(forceFinish);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Advisor model call failed, falling back to rules");
            _state.ChatLog.Add(new ChatEntry
            {
                Text = "Atsiprašau, AI patarėjas laikinai nepasiekiamas – tęsiu standartine apklausa."
            });

            if ((_state.PendingQuestionId is null || forceFinish) && !IsFinished)
            {
                RuleBasedTurn(forceFinish);
            }
        }

        _freeTextTurn = false;
        await _state.SaveAsync();
    }

    private void AddAssistantText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // ask_question already added the bubble with the intro – avoid duplicates.
        var last = _state.ChatLog.LastOrDefault();
        if (last is { FromUser: false } && last.Text.Contains(text.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        // A question bubble already explains itself; extra model chatter is shown only when it answers
        // something the user wrote.
        if (_askedThisTurn && !_freeTextTurn)
        {
            return;
        }

        // Models sometimes write text after ask_question; show it before the question bubble.
        var entry = new ChatEntry { Text = text.Trim() };
        var questionIndex = _askedThisTurn ? _state.ChatLog.FindLastIndex(e => e.QuestionId is not null) : -1;
        if (questionIndex >= 0)
        {
            _state.ChatLog.Insert(questionIndex, entry);
        }
        else
        {
            _state.ChatLog.Add(entry);
        }
    }

    /// <summary>Rule-based fallback: next visible unanswered question in funnel order, else finish.</summary>
    private void RuleBasedTurn(bool forceFinish = false)
    {
        var limitReached = forceFinish || _state.AskedQuestionIds.Count >= _options.MaxQuestions;
        var next = limitReached ? null : AvailableQuestions().FirstOrDefault();

        // The closing free-text question is asked even when the question limit is reached.
        if (next is null && !forceFinish && ExtraInfoPending())
        {
            next = Questionnaire.GetQuestion(Questionnaire.ExtraInfoId);
        }

        if (next is not null)
        {
            ShowQuestion(next, null);
            return;
        }

        _state.PendingQuestionId = null;
        var profile = _calculator.BuildProfile(_state);
        var recommendation = _calculator.RuleBasedRecommendation(profile, _calculator.Estimate(profile));
        _state.Recommendation = recommendation;
        if (_solar.Evaluate(_state) is { } solar)
        {
            recommendation.SummaryMarkdown = recommendation.SummaryMarkdown.TrimEnd() + Environment.NewLine + Environment.NewLine + _solar.Paragraph(solar);
        }
        _state.ChatLog.Add(new ChatEntry { Text = "Ačiū! Rekomendacija paruošta." });
    }

    /// <summary>The closing free-text question has not been shown or answered yet.</summary>
    private bool ExtraInfoPending() =>
        Questionnaire.GetQuestion(Questionnaire.ExtraInfoId) is { } q && !_state.IsQuestionAnswered(q);

    private IEnumerable<Question> AvailableQuestions() =>
        Questionnaire.Questions.Where(q => _state.IsVisible(q) && !_state.IsQuestionAnswered(q));

    /// <summary>
    /// The next question of the scheme (not supplementary, not the closing free-text one). Scheme
    /// questions are asked strictly in order; supplementary ones may be asked or skipped any time.
    /// </summary>
    private Question? NextSchemeQuestion() =>
        AvailableQuestions().FirstOrDefault(q => !q.Supplementary && q.Id != Questionnaire.ExtraInfoId);

    private void ShowQuestion(Question question, string? intro)
    {
        _state.PendingQuestionId = question.Id;
        if (!_state.AskedQuestionIds.Contains(question.Id))
        {
            _state.AskedQuestionIds.Add(question.Id);
        }

        _state.ChatLog.Add(new ChatEntry { Text = intro?.Trim() ?? string.Empty, QuestionId = question.Id });
    }

    /// <summary>Compact state injected into every turn so the model never asks what is known.</summary>
    private string BuildStateSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[BŪSENA] Užduota klausimų: {_state.AskedQuestionIds.Count}/{_options.MaxQuestions}.");

        if (_state.Analysis is { Success: true } a)
        {
            sb.Append($"Įkeltas ESO failas: {a.Readings} įrašų, {a.Months} mėn.");
            if (a.MonthlyAverageKwh is { } avg) sb.Append($", vidutiniškai {avg:0} kWh/mėn.");
            if (a.NightShare is { } night) sb.Append($", naktinis vartojimas {night:P0}");
            if (a.ExportedMonthlyKwh is { } exported) sb.Append($"; tai pirkimas iš tinklo, į tinklą atiduodama {exported:0} kWh/mėn. (turi saulės elektrinę)");
            sb.AppendLine(".");
        }

        var profile = _calculator.BuildProfile(_state);
        var solarLine = profile.Solar switch
        {
            SolarStatus.Own => $"turi elektrinę{(profile.ExistingKwp is { } kw ? $" ~{kw:0.#} kWp" : " (galia nežinoma)")}, kaupiklis {profile.Battery} – prieš finish iškviesk simulate_solar",
            SolarStatus.Interested => $"DOMINA ({(profile.LowGridCapacity ? "galima apkrova < 5 kW – tik kaupiklis" : profile.WantsPanels && profile.WantsBattery ? "panelės ir kaupiklis" : profile.WantsPanels ? "tik panelės" : "tik kaupiklis")}) – prieš finish iškviesk simulate_solar",
            _ => !profile.IsHouse && _state.GetAnswer(1).SelectedOptions.Count > 0 ? "butas – pagal schemą saulė/kaupiklis nevertinami"
                : _state.GetAnswer(8).SelectedOptions.Count > 0 ? "nedomina" : "nežinoma (Q8)"
        };
        sb.AppendLine($"Saulė/kaupiklis: {solarLine}.");

        if (_state.EsoConnection is { Success: true } eso)
        {
            sb.AppendLine($"ESO prijungimas (obj. {eso.ObjectNumber}): įrengta {eso.InstalledGenerationKw:0.##} kW, leistina generuoti {eso.AllowedGenerationKw:0.##} kW; {eso.Message}");
        }
        else if (_state.Analysis?.ObjectNumber is { } objectNumber)
        {
            sb.AppendLine($"ESO objekto nr. iš failo: {objectNumber} (prijungimo galimybės dar netikrintos – check_grid_connection).");
        }

        var answered = _state.AnsweredQuestions();
        sb.AppendLine(answered.Count == 0 ? "Žinoma: nieko." : "Žinoma:");
        foreach (var q in answered)
        {
            var answer = _state.GetAnswer(q.Id);
            sb.AppendLine($"- Q{q.Id} {q.Text} = {QuestionnaireState.FormatAnswer(answer)}{(answer.IsAuto ? " (iš failo)" : "")}");
        }

        // The closing free-text question is shown by the code before finish, never chosen by the model.
        // Scheme questions only in order: the next one plus any supplementary question.
        var next = NextSchemeQuestion();
        var available = AvailableQuestions()
            .Where(q => q.Id != Questionnaire.ExtraInfoId && (q.Supplementary || q.Id == next?.Id))
            .ToList();
        sb.AppendLine(next is null
            ? "Schemos klausimai: visi atsakyti."
            : $"KITAS pagal schemą: Q{next.Id} (schemos klausimai užduodami tik iš eilės).");
        sb.AppendLine(available.Count == 0 ? "Galimi klausimai: nebėra – kviesk estimate_plans ir finish." : "Galimi klausimai:");
        foreach (var q in available)
        {
            sb.AppendLine($"- Q{q.Id} [{q.Section}] {q.Text} — {q.Purpose}");
        }

        return sb.ToString();
    }

    // ── Tools ───────────────────────────────────────────────────────────

    [Description("Grąžina viską, kas žinoma apie vartotoją, ir šiuo metu galimus klausimus su jų paskirtimi.")]
    private string GetProfile() => BuildStateSummary();

    [Description("Parodo vartotojui klausimą iš banko su atsakymų variantais. Po šio įrankio ėjimą baik.")]
    private string AskQuestion(
        [Description("Klausimo ID iš sąrašo „Galimi klausimai“")] int questionId,
        [Description("Vienas trumpas sakinys (iki 20 žodžių), kodėl klausi")] string intro)
    {
        if (_finishedThisTurn || _state.Recommendation is not null)
        {
            return "KLAIDA: rekomendacija jau pateikta.";
        }

        if (_askedThisTurn)
        {
            return "KLAIDA: šiame ėjime klausimas jau užduotas. Baik ėjimą be papildomo teksto.";
        }

        if (Questionnaire.GetQuestion(questionId) is not { } question)
        {
            return $"KLAIDA: klausimo Q{questionId} nėra banke.";
        }

        if (questionId == Questionnaire.ExtraInfoId)
        {
            return "KLAIDA: paskutinis laisvos informacijos klausimas parodomas automatiškai, kai kviesi finish. Rinkis kitą klausimą arba kviesk finish.";
        }

        if (!_state.IsVisible(question))
        {
            return $"KLAIDA: Q{questionId} dabar negalimas (neaktualus arba dar neatsakyta, nuo ko jis priklauso).";
        }

        if (_state.IsQuestionAnswered(question))
        {
            return $"KLAIDA: į Q{questionId} jau atsakyta: {QuestionnaireState.FormatAnswer(_state.GetAnswer(questionId))}.";
        }

        if (!question.Supplementary && NextSchemeQuestion() is { } next && next.Id != questionId)
        {
            return $"KLAIDA: pagal schemą pirmiausia klausk Q{next.Id} ({next.Text}).";
        }

        if (_state.PendingQuestionId == questionId)
        {
            _askedThisTurn = true;
            return "Šis klausimas jau rodomas ir laukia atsakymo. Baik ėjimą.";
        }

        if (_state.AskedQuestionIds.Count >= _options.MaxQuestions)
        {
            return "KLAIDA: klausimų limitas pasiektas. Kviesk estimate_plans ir finish.";
        }

        ShowQuestion(question, intro);
        _askedThisTurn = true;
        return "Klausimas parodytas vartotojui. Baik ėjimą DABAR ir nerašyk jokio teksto.";
    }

    [Description("Užfiksuoja atsakymą, kurį vartotojas pasakė laisvu tekstu, kad to klausimo nereikėtų klausti.")]
    private string RecordAnswer(
        [Description("Klausimo ID")] int questionId,
        [Description("Tikslus atsakymo variantas iš to klausimo variantų")] string option)
    {
        if (Questionnaire.GetQuestion(questionId) is not { } question)
        {
            return $"KLAIDA: klausimo Q{questionId} nėra banke.";
        }

        var match = question.Options.FirstOrDefault(o => string.Equals(o, option.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return $"KLAIDA: tokio varianto nėra. Galimi: {string.Join(" | ", question.Options)}";
        }

        if (question.IsMultiSelect)
        {
            _state.ToggleMulti(questionId, match, true, question.ExclusiveOption);
        }
        else
        {
            _state.SetSingle(questionId, match);
        }

        if (_state.PendingQuestionId == questionId)
        {
            _state.PendingQuestionId = null;
        }

        return $"Užfiksuota: Q{questionId} = {match}.";
    }

    [Description("Deterministiškai suskaičiuoja visus leistinus derinius (ESO planas × zonos × tiekėjo planas): pilną sąskaitą su ESO dalimi, " +
                 "asmeninę svertinę biržos kainą, ESO plano parinkimo paaiškinimus, rizikas ir skaičiuoklės siūlymą.")]
    private string EstimatePlans()
    {
        var profile = _calculator.BuildProfile(_state);
        var text = _calculator.Describe(profile, _calculator.Estimate(profile));

        // Solar numbers are always attached so the model has them even without simulate_solar.
        if (_solar.Evaluate(_state) is { } r)
        {
            text += $"{Environment.NewLine}SAULĖ/KAUPIKLIS: {r.Verdict} {r.ChosenLabel}: investicija {r.Chosen.Capex:0} €, " +
                    $"1 m. sutaupymas {r.Chosen.Year1Savings:0} €, atsipirkimas {(r.Chosen.PaybackYears is { } y ? $"{y:0.#} m." : "neatsiperka")}. " +
                    $"Visa sąskaita: {r.MonthlyBillAfter.Sum() / 12:0} €/mėn. vietoj {r.MonthlyBillBefore.Sum() / 12:0} €/mėn. " +
                    $"(pigiausias derinys po pakeitimo: {r.PlanLabel}{(r.BestSettlement is { } s ? $", {s.Label}" : "")}). Detaliau – simulate_solar.";
        }

        return text;
    }

    [Description("Patikrina ESO tinkle saulės elektrinės prijungimo galimybes: įrengtą ir leistiną generuoti galią bei ESO vertinimą. " +
                 "Kviesk, kai vartotojui aktuali saulė ir žinomas 8 skaitmenų objekto numeris (iš failo arba vartotojas jį parašė).")]
    private async Task<string> CheckGridConnectionAsync(
        [Description("8 skaitmenų ESO objekto numeris; neprivaloma, jei jis yra įkeltame faile")] string? objectNumber = null)
    {
        var nr = string.IsNullOrWhiteSpace(objectNumber) ? _state.Analysis?.ObjectNumber : objectNumber.Trim();
        if (nr is null)
        {
            return "KLAIDA: objekto numeris nežinomas. Paprašyk vartotojo jį parašyti (ESO savitarna → Objektai) arba įkelti ESO failą.";
        }

        var info = await _eso.CheckAsync(nr);
        if (!info.Success)
        {
            return $"KLAIDA: {info.Error}";
        }

        _state.ApplyEsoConnection(info);
        return $"ESO obj. {info.ObjectNumber}: įrengta generuojanti galia {info.InstalledGenerationKw:0.##} kW, leistina generuoti {info.AllowedGenerationKw:0.##} kW, " +
               $"leistinoji vartojimo galia {info.AllowedConsumptionKw:0.##} kW." +
               (info.ExportLimitKw is { } lim ? $" Naujai elektrinei atidavimas į tinklą ribojamas iki {lim:0.#} kW." : "") +
               $" ESO vertinimas: {info.Message} (Q8/Q26 atnaujinti automatiškai, jei elektrinė jau yra.)";
    }

    [Description("Saulės elektrinės ir/ar kaupiklio atsipirkimas pagal vartotojo duomenis (įranga + APVA, 3 ESO pasaugojimo būdai ir grynasis atsiskaitymas). " +
                 "Be argumentų – randa geriausią variantą; argumentus perduok tik kai vartotojas pats nurodo dydį (pvz. „o jei 6 kW ir 10 kWh?“). " +
                 "Turintiems elektrinę vertinamas tik kaupiklis; namui, kuriam domina tik kaupiklis (arba galima apkrova < 5 kW) – kaupiklis be elektrinės, tik su dinaminiu tarifu.")]
    private string SimulateSolar(
        [Description("Elektrinės galia kWp (neprivaloma)")] double? pvKwp = null,
        [Description("Kaupiklio talpa kWh (neprivaloma)")] double? batteryKwh = null)
    {
        var report = _solar.Evaluate(_state, pvKwp, batteryKwh);
        if (report is null)
        {
            var q8 = QuestionnaireState.FormatAnswer(_state.GetAnswer(8));
            return $"Saulės/kaupiklio skaičiavimas neaktualus (būstas = {QuestionnaireState.FormatAnswer(_state.GetAnswer(1))}, Q8 = {q8}). " +
                   "Pagal schemą saulė ir kaupiklis vertinami tik namui, atsakiusiam „Taip, domina“ arba turinčiam elektrinę.";
        }

        return _solar.Describe(report);
    }

    [Description("Pateikia galutinę rekomendaciją ir baigia pokalbį. Kviesk tik po estimate_plans.")]
    private string Finish(
        [Description("Rekomenduojamo derinio ID iš estimate_plans, pvz. N2:ENE-F12z2")] string planId,
        [Description("Išvada Markdown formatu pagal instrukcijose nurodytą struktūrą")] string summaryMarkdown,
        [Description("Pasitikėjimas: Aukštas, Vidutinis arba Žemas")] string confidence)
    {
        var profile = _calculator.BuildProfile(_state);
        var estimates = _calculator.Estimate(profile);
        var plan = estimates.FirstOrDefault(e => string.Equals(e.Id, planId.Trim().Trim('[', ']'), StringComparison.OrdinalIgnoreCase));
        if (plan is null)
        {
            var top = string.Join(", ", estimates.OrderBy(e => e.Score).Take(5).Select(e => e.Id));
            return $"KLAIDA: derinio „{planId}“ nėra. Naudok ID iš estimate_plans (pvz. {top}).";
        }

        if (string.IsNullOrWhiteSpace(summaryMarkdown))
        {
            return "KLAIDA: summaryMarkdown tuščias.";
        }

        // The scheme is the backbone: every scheme question on the user's branch must be answered.
        if (!_forceFinish && _state.AskedQuestionIds.Count < _options.MaxQuestions && NextSchemeQuestion() is { } missing)
        {
            return $"KLAIDA: pagal schemą dar neatsakyta Q{missing.Id} ({missing.Text}) – paklausk jo prieš išvadą.";
        }

        // Before the conclusion the user always gets the chance to add anything in their own words.
        if (!_forceFinish && ExtraInfoPending() && Questionnaire.GetQuestion(Questionnaire.ExtraInfoId) is { } extra)
        {
            if (_state.PendingQuestionId != extra.Id)
            {
                ShowQuestion(extra, "Prieš pateikdamas išvadą – gal norite ką nors pridurti?");
            }

            _askedThisTurn = true;
            return $"Prieš išvadą vartotojui parodytas paskutinis klausimas Q{extra.Id} (laisva informacija). " +
                   "Baik ėjimą be teksto. Kai atsakys – užfiksuok faktus record_answer, jei reikia perskaičiuok ir kviesk finish.";
        }

        _state.PendingQuestionId = null;
        _state.Recommendation = new Recommendation
        {
            PlanType = plan.Type,
            Supplier = plan.Supplier,
            PlanId = plan.Id,
            PlanLabel = PlanCalculator.FullLabel(plan),
            SummaryMarkdown = summaryMarkdown.Trim(),
            Confidence = confidence.Trim(),
            ByAi = true
        };
        _finishedThisTurn = true;
        _state.ChatLog.Add(new ChatEntry { Text = "Rekomendacija paruošta – peržiūrėkite rezultatus." });

        return "Rekomendacija išsaugota. Baik ėjimą be papildomo teksto.";
    }
}

/// <summary>Loads the system prompt from <c>Prompts/advisor-system.md</c> once.</summary>
public sealed class AdvisorPrompt
{
    private readonly string _template;

    public AdvisorPrompt(IWebHostEnvironment environment)
    {
        var path = Path.Combine(environment.ContentRootPath, "Prompts", "advisor-system.md");
        _template = File.ReadAllText(path);
    }

    public string Render(int maxQuestions) => _template.Replace("{MaxQuestions}", maxQuestions.ToString());
}
