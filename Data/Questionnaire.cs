using vamos.Models;

namespace vamos.Data;

/// <summary>
/// Single source of truth for the survey tree and demo options.
/// <para>
/// The list is already in "tree" order: a question may be hidden by
/// <see cref="Question.ShowWhen"/> conditions that refer to earlier questions.
/// Replace the option strings here to change the questionnaire without touching the UI.
/// </para>
/// </summary>
public static class Questionnaire
{
    private const string None = "Neturiu";
    private const string NoneOrNotPlanned = "Neturiu ir neplanuoju";

    // Option labels reused in tree conditions (kept as constants to avoid typos).
    public const string PlanFixed = "Noriu fiksuotos, stabilios kainos – nenoriu sekti biržos";
    public const string PlanMiddle = "Norėčiau sutaupyti, bet nenoriu daug keisti įpročių";
    public const string PlanOptimize = "Esu linkęs prisiimti riziką ir aktyviai optimizuoti išlaidas";

    public const string EvChargeAtHome = "Taip, kraunu namuose";
    public const string NoSolar = NoneOrNotPlanned;
    public const string CryptoMining = "Kriptovaliutų kasimo įranga";
    public const string NoRoutineChange = "Ne, nenoriu keisti rutinos";

    /// <summary>All questions in the exact order the tree is walked.</summary>
    public static IReadOnlyList<Question> Questions { get; } = new List<Question>
    {
        // ── 1 blokas: Būstas ir vartojimas ──────────────────────────────
        new()
        {
            Id = 1, Section = "Būstas ir vartojimas",
            Text = "Koks jūsų būsto tipas ir apytikslė kvadratūra?",
            Options = new[]
            {
                "Butas iki 50 m²", "Butas 50–80 m²", "Butas virš 80 m²",
                "Namas iki 100 m²", "Namas 100–200 m²", "Namas virš 200 m²"
            }
        },
        new()
        {
            Id = 2, Section = "Būstas ir vartojimas",
            Text = "Kiek asmenų nuolat gyvena jūsų namuose?",
            Options = new[] { "1", "2", "3", "4", "5 ar daugiau" }
        },
        new()
        {
            Id = 3, Section = "Būstas ir vartojimas",
            Text = "Koks apytikslis jūsų vidutinis mėnesinis elektros suvartojimas?",
            Options = new[] { "Iki 150 kWh", "150–300 kWh", "Virš 300 kWh", "Nežinau" }
        },
        new()
        {
            Id = 4, Section = "Būstas ir vartojimas",
            Text = "Kokia jūsų namų šildymo sistema?",
            Options = new[]
            {
                "Šilumos siurblys", "Gamtinės dujos", "Centrinis šildymas",
                "Elektrinis šildymas (radiatoriai, grindys)",
                "Kietasis kuras (malkos, granulės)", "Nėra atskiro šildymo"
            }
        },
        new()
        {
            Id = 5, Section = "Būstas ir vartojimas",
            Text = "Kaip ruošiate karštą vandenį?",
            Options = new[]
            {
                "Elektrinis boileris", "Šilumos siurblys",
                "Centrinis (šilumos tinklai)", "Dujinis katilas"
            }
        },

        // ── 2 blokas: Didieji vartotojai ────────────────────────────────
        new()
        {
            Id = 6, Section = "Didieji vartotojai",
            Text = "Ar namuose įdiegta rekuperacinė vėdinimo arba kondicionavimo sistema?",
            IsMultiSelect = true, ExclusiveOption = None,
            Options = new[] { "Rekuperacinė vėdinimo sistema", "Kondicionierius", None }
        },
        new()
        {
            Id = 7, Section = "Didieji vartotojai",
            Text = "Ar turite (arba planuojate įsigyti) elektromobilį, kurį kraunate namuose?",
            Options = new[]
            {
                EvChargeAtHome, "Taip, bet kraunu ne namuose",
                "Ne, bet planuoju įsigyti", "Ne"
            }
        },
        new()
        {
            Id = 18, Section = "Didieji vartotojai",
            Text = "Ar galite valdyti elektromobilio krovimo laiką (pvz., krauti naktį)?",
            ShowWhen = new[] { new ShowCondition { QuestionId = 7, HasAny = new[] { EvChargeAtHome } } },
            Options = new[] { "Taip, kraunu tik naktį", "Taip, galiu atidėti", "Ne, kraunu kada tenka" }
        },
        new()
        {
            Id = 8, Section = "Didieji vartotojai",
            Text = "Ar turite nuosavą saulės/vėjo elektrinę arba esate įsigiję nutolusio parko dalį?",
            IsMultiSelect = true, ExclusiveOption = NoSolar,
            Options = new[]
            {
                "Nuosava saulės/vėjo elektrinė", "Nutolusio saulės/vėjo parko dalis", NoSolar
            }
        },
        new()
        {
            Id = 19, Section = "Didieji vartotojai",
            Text = "Ar turite elektros kaupiklį (bateriją)?",
            ShowWhen = new[] { new ShowCondition { QuestionId = 8, HasNone = new[] { NoSolar } } },
            Options = new[] { "Taip, turiu", "Ne, bet planuoju", "Ne" }
        },
        new()
        {
            Id = 9, Section = "Didieji vartotojai",
            Text = "Ar turite daug elektros reikalaujančių pramogų įrenginių?",
            IsMultiSelect = true, ExclusiveOption = None,
            Options = new[] { "Elektrinė pirtis", "Šildomas lauko baseinas", "Sūkurinė vonia", None }
        },
        new()
        {
            Id = 10, Section = "Didieji vartotojai",
            Text = "Ar turite nuolat veikiančios kompiuterinės technikos?",
            IsMultiSelect = true, ExclusiveOption = None,
            Options = new[] { "Namų serveris / NAS", CryptoMining, None }
        },
        new()
        {
            Id = 11, Section = "Didieji vartotojai",
            Text = "Ar dažnai ir ilgai naudojate galingus kompiuterius žaidimams ar vaizdo redagavimui?",
            // Analogiškas Q10: jei turi kasimo įrangą (24/7), žaidimų PC nebeaktualus.
            ShowWhen = new[] { new ShowCondition { QuestionId = 10, HasNone = new[] { CryptoMining } } },
            Options = new[]
            {
                "Taip, kasdien kelias valandas", "Taip, kelis kartus per savaitę",
                "Retai", "Nenaudoju"
            }
        },

        // ── 3 blokas: Požiūris į sąskaitas (vartai) ─────────────────────
        new()
        {
            Id = 17, Section = "Požiūris į sąskaitas",
            Text = "Koks jūsų požiūris į sąskaitas už elektrą ir jų valdymą?",
            Options = new[] { PlanFixed, PlanMiddle, PlanOptimize }
        },

        // ── 4 blokas: Lankstumas ir įpročiai ────────────────────────────
        new()
        {
            Id = 14, Section = "Lankstumas ir įpročiai",
            Text = "Koks jūsų ir šeimos įprastas dienos ritmas darbo dienomis?",
            Options = new[]
            {
                "Namuose nieko nėra 8–17 val.", "Dirbu iš namų",
                "Mišrus (keliomis dienomis iš namų)", "Namuose būna beveik visą dieną"
            }
        },
        new()
        {
            Id = 15, Section = "Lankstumas ir įpročiai",
            Text = "Kada dažniausiai atliekate daugiausiai elektros reikalaujančius buities darbus?",
            ShowWhen = new[] { new ShowCondition { QuestionId = 17, HasNone = new[] { PlanFixed } } },
            Options = new[]
            {
                "Ryte (6–9 val.)", "Dieną (9–17 val.)", "Vakare (17–22 val.)",
                "Naktį (22–6 val.)", "Savaitgaliais"
            }
        },
        new()
        {
            Id = 16, Section = "Lankstumas ir įpročiai",
            Text = "Jei naktį arba savaitgalį elektra būtų kelis kartus pigesnė, ar pakeistumėte rutiną?",
            ShowWhen = new[] { new ShowCondition { QuestionId = 17, HasNone = new[] { PlanFixed } } },
            Options = new[]
            {
                "Taip, tikrai pakeisčiau rutiną", "Galbūt, išbandyčiau", NoRoutineChange
            }
        },
        new()
        {
            Id = 12, Section = "Lankstumas ir įpročiai",
            Text = "Ar jūsų stambioji buitinė technika turi atidėto starto („Delay Timer“) funkciją?",
            ShowWhen = new[]
            {
                new ShowCondition { QuestionId = 17, HasNone = new[] { PlanFixed } },
                new ShowCondition { QuestionId = 16, HasNone = new[] { NoRoutineChange } }
            },
            Options = new[] { "Taip, dauguma prietaisų", "Taip, kai kurie", "Ne", "Nežinau" }
        },
        new()
        {
            Id = 13, Section = "Lankstumas ir įpročiai",
            Text = "Ar naudojate išmaniuosius namų sprendimus elektros vartojimui automatizuoti?",
            ShowWhen = new[]
            {
                new ShowCondition { QuestionId = 17, HasAny = new[] { PlanMiddle, PlanOptimize } }
            },
            IsMultiSelect = true, ExclusiveOption = "Nenaudoju",
            Options = new[] { "Išmaniosios rozetės", "Automatinis šilumos siurblio valdymas", "Nenaudoju" }
        }
    };

    public static int FirstQuestionId => Questions[0].Id;

    public static Question? GetQuestion(int id) => Questions.FirstOrDefault(q => q.Id == id);
}
