using vamos.Models;

namespace vamos.Data;

/// <summary>
/// Single source of truth for the question bank.
/// <para>
/// The list follows the project's question scheme: duomenų įkėlimas → būstas (butas / namas) → asmenys →
/// elektros prietaisai (+ elektromobilis namui) → skaitiklio tipas → (namui) saulės panelės / kaupiklis →
/// galima apkrova → kas domina → pasiūlymas. Zones, ESO plans, the tariff and the storage mode are
/// calculated, not asked. <see cref="Question.Supplementary"/> questions (heating, attitude to bills) are
/// extras the AI may skip. The classic wizard walks the list top to bottom; the AI advisor picks from it
/// by id. <see cref="Question.ShowWhen"/> conditions are hard rules for both: a hidden question is never asked.
/// </para>
/// </summary>
public static class Questionnaire
{
    // Option labels reused in tree conditions and in PlanCalculator (kept as constants to avoid typos).
    public static readonly string[] HouseOptions = { "Namas iki 100 m²", "Namas 100–200 m²", "Namas virš 200 m²" };

    public const string PlanFixed = "Noriu fiksuotos, stabilios kainos – nenoriu sekti biržos";
    public const string PlanMiddle = "Norėčiau sutaupyti, bet nenoriu daug keisti įpročių";
    public const string PlanOptimize = "Esu linkęs prisiimti riziką ir aktyviai optimizuoti išlaidas";

    public const string MonthlyLow = "Iki 150 kWh";
    public const string MonthlyMid = "150–300 kWh";
    public const string MonthlyHigh = "300–600 kWh";
    public const string MonthlyVeryHigh = "Virš 600 kWh";
    public const string DontKnow = "Nežinau";

    public const string HeatPump = "Šilumos siurblys";
    public const string ElectricHeating = "Elektrinis šildymas (radiatoriai, grindys)";

    public const string ElectricBoiler = "Elektrinis vandens šildytuvas (boileris)";
    public const string ElectricStove = "Indukcinė / elektrinė viryklė";
    public const string Recuperation = "Rekuperacinė vėdinimo sistema";
    public const string AirConditioner = "Kondicionierius";
    public const string Sauna = "Elektrinė pirtis";
    public const string Pool = "Šildomas lauko baseinas";
    public const string Jacuzzi = "Sūkurinė vonia";
    public const string HomeServer = "Namų serveris / NAS";
    public const string CryptoMining = "Kriptovaliutų kasimo įranga";
    public const string PowerfulPc = "Galingas kompiuteris (žaidimai, vaizdo montavimas)";
    public const string NoAppliances = "Nė vieno iš šių";

    public const string EvChargeAtHome = "Taip, kraunu namuose";

    public const string SmartMeterYes = "Išmanusis ESO skaitiklis";
    public const string SimpleMeter = "Paprastas (senas) skaitiklis";

    public const string SolarInterested = "Taip, domina";
    public const string OwnSolar = "Jau turiu saulės elektrinę";
    public const string SolarNo = "Ne";

    public const string CapacityOk = "5 kW ar daugiau";
    public const string CapacityLow = "Mažiau nei 5 kW";

    public const string WantPanels = "Saulės panelės";
    public const string WantBattery = "Kaupiklis";
    public const string WantBoth = "Abu";

    public const string BatteryYes = "Taip, turiu";

    public const string DoubleBillUnacceptable = "Būtų nepriimtina – biudžetas griežtas";
    public const string DoubleBillOk = "Gerai, jei per metus vis tiek sutaupau";

    public const int CapacityId = 33;
    public const int InterestId = 34;

    /// <summary>The closing free-text question, always asked last.</summary>
    public const int ExtraInfoId = 32;
    public const string NothingToAdd = "Neturiu ką pridėti";

    /// <summary>Shown at the start so the user knows they can add anything at the end.</summary>
    public const string StartNotice =
        "Pabaigoje galėsite laisvai papasakoti apie save tai, ko neklausėme – pvz. planuojamus pokyčius, " +
        "neįprastą įrangą ar darbo grafiką. Į tai atsižvelgsime pateikdami rekomendaciją.";

    private static ShowCondition IsHouse => new() { QuestionId = 1, HasAny = HouseOptions };

    /// <summary>All questions in the order of the scheme.</summary>
    public static IReadOnlyList<Question> Questions { get; } = new List<Question>
    {
        // ── Būstas ──────────────────────────────────────────────────────
        new()
        {
            Id = 1, Section = "Būstas",
            Text = "Koks jūsų būsto tipas ir apytikslė kvadratūra?",
            Purpose = "Schemos šaka: butas (be saulės/kaupiklio) ar namas; vartojimo masto proxy, jei nėra ESO failo.",
            Options = new[]
            {
                "Butas iki 50 m²", "Butas 50–80 m²", "Butas virš 80 m²",
                HouseOptions[0], HouseOptions[1], HouseOptions[2]
            }
        },
        new()
        {
            Id = 2, Section = "Būstas",
            Text = "Kiek asmenų nuolat gyvena jūsų namuose?",
            Purpose = "Vartojimo masto proxy, kai nėra ESO failo.",
            Options = new[] { "1", "2", "3", "4", "5 ar daugiau" }
        },
        new()
        {
            Id = 3, Section = "Būstas",
            Text = "Kiek elektros vidutiniškai sunaudojate per mėnesį?",
            Purpose = "Suvartojimas – svarbiausias skaičius. Klausiama tik tada, kai neįkeltas ESO failas; " +
                      "jei žmogus nežino – įvertinama apytiksliai pagal būstą, gyventojus ir prietaisus.",
            Notes = new[]
            {
                new QuestionNote
                {
                    Text = "Skaičių rasite elektros sąskaitoje arba mano.eso.lt. Jei žinote tikslų – įrašykite per „Kita“ (kWh per mėnesį). " +
                           "Nežinote – pasirinkite „Nežinau“, įvertinsime apytiksliai."
                }
            },
            Options = new[] { MonthlyLow, MonthlyMid, MonthlyHigh, MonthlyVeryHigh, DontKnow }
        },

        // ── Elektros prietaisai ─────────────────────────────────────────
        new()
        {
            Id = 4, Section = "Elektros prietaisai", Supplementary = true,
            Text = "Kokia jūsų namų šildymo sistema?",
            Purpose = "Papildomas: elektrinis šildymas / ŠS – didžiausias vartotojas žiemą, kai biržos kainos aukščiausios.",
            Options = new[]
            {
                HeatPump, "Gamtinės dujos", "Centrinis šildymas",
                ElectricHeating, "Kietasis kuras (malkos, granulės)", "Nėra atskiro šildymo"
            }
        },
        new()
        {
            Id = 5, Section = "Elektros prietaisai",
            Text = "Kokius iš šių elektros prietaisų naudojate namuose?",
            Purpose = "Įrenginiai, kurie vartoja elektrą: nuo jų priklauso suvartojimas ir kada jis vyksta (boileris – lengvai perkeliamas į naktį, " +
                      "viryklė ir kompiuteriai – vakaras, serveris / kasimas – visą parą).",
            IsMultiSelect = true, ExclusiveOption = NoAppliances,
            Options = new[]
            {
                ElectricBoiler, ElectricStove, Recuperation, AirConditioner, Sauna, Pool, Jacuzzi,
                HomeServer, CryptoMining, PowerfulPc, NoAppliances
            }
        },
        new()
        {
            Id = 7, Section = "Elektros prietaisai",
            Text = "Ar turite (arba planuojate įsigyti) elektromobilį, kurį kraunate namuose?",
            Purpose = "Tik namui: EV krovimas namuose – didelis ir lengvai perkeliamas į naktį vartojimas.",
            ShowWhen = new[] { IsHouse },
            Options = new[] { EvChargeAtHome, "Taip, bet kraunu ne namuose", "Ne, bet planuoju įsigyti", "Ne" }
        },

        // ── Skaitiklis ──────────────────────────────────────────────────
        new()
        {
            Id = 20, Section = "Skaitiklis",
            Text = "Koks jūsų elektros skaitiklis?",
            Purpose = "Paprastas skaitiklis – tik 1 arba 2 zonos; išmanusis – ir 4 zonos, valandinė biržos nauda. " +
                      "Saulės elektrinei ar kaupikliui reikia išmaniojo (ESO keičia nemokamai).",
            Options = new[] { SmartMeterYes, SimpleMeter, "Nežinau" }
        },

        // ── Saulė ir kaupiklis (tik namui) ──────────────────────────────
        new()
        {
            Id = 8, Section = "Saulės panelės ir kaupiklis",
            Text = "Ar jums aktualios saulės panelės arba kaupiklis?",
            Purpose = "Tik namui. „Taip“ – skaičiuojama investicija, APVA ir atsipirkimas; „jau turiu“ – parenkamas planas ir pasaugojimo būdas esamai elektrinei.",
            ShowWhen = new[] { IsHouse },
            Options = new[] { SolarInterested, OwnSolar, SolarNo }
        },
        new()
        {
            Id = CapacityId, Section = "Saulės panelės ir kaupiklis",
            Text = "Kiek galios jūsų objekte galima prijungti pagal ESO (galima apkrova rajone)?",
            Purpose = "≥ 5 kW – galima saulės elektrinė ir (ar) kaupiklis; < 5 kW – tik kaupiklis. Su ESO failu tikrinama automatiškai.",
            ShowWhen = new[] { new ShowCondition { QuestionId = 8, HasAny = new[] { SolarInterested } } },
            ShowEsoCheck = true,
            Notes = new[]
            {
                new QuestionNote
                {
                    Text = "Saulės elektrinei ar kaupikliui reikia išmaniojo skaitiklio – ESO jį pakeičia nemokamai.",
                    ShowWhen = new[] { new ShowCondition { QuestionId = 20, HasNone = new[] { SmartMeterYes } } }
                },
                new QuestionNote
                {
                    Text = "Galimą apkrovą patikrinkite ESO įrankyje pagal objekto numerį (rasite ESO savitarnoje). Jei įkėlėte ESO failą – patikrinsime patys.",
                    Url = "https://www.eso.lt/namams/gaminantis-vartotojas/saules-elektrines-prijungimo-galimybes/362",
                    LinkText = "ESO: saulės elektrinės prijungimo galimybės"
                }
            },
            Options = new[] { CapacityOk, CapacityLow, "Nežinau" }
        },
        new()
        {
            Id = InterestId, Section = "Saulės panelės ir kaupiklis",
            Text = "Kas jus domina?",
            Purpose = "Panelės – tarifas dinaminis arba fiksuotas; kaupiklis – tik dinaminis (biržos) tarifas.",
            ShowWhen = new[]
            {
                new ShowCondition { QuestionId = 8, HasAny = new[] { SolarInterested } },
                new ShowCondition { QuestionId = CapacityId, HasNone = new[] { CapacityLow } }
            },
            Options = new[] { WantPanels, WantBattery, WantBoth }
        },
        new()
        {
            Id = 26, Section = "Saulės panelės ir kaupiklis",
            Text = "Kokia jūsų saulės elektrinės galia?",
            Purpose = "Esamai elektrinei: kiek metinio vartojimo padengia sava gamyba (ESO patikrinimas užpildo automatiškai).",
            ShowWhen = new[] { new ShowCondition { QuestionId = 8, HasAny = new[] { OwnSolar } } },
            Options = new[] { "Iki 5 kW", "5–10 kW", "Virš 10 kW", "Nežinau" }
        },
        new()
        {
            Id = 19, Section = "Saulės panelės ir kaupiklis",
            Text = "Ar prie elektrinės turite kaupiklį (namų bateriją)?",
            Purpose = "Tik ~1 % gaminančių turi kaupiklį – jei neturi, įvertinsime, ar verta įsirengti (su dinaminiu tarifu).",
            ShowWhen = new[] { new ShowCondition { QuestionId = 8, HasAny = new[] { OwnSolar } } },
            Options = new[] { BatteryYes, "Ne" }
        },

        // ── Požiūris į sąskaitas (papildomi, prieš tarifo parinkimą) ────
        new()
        {
            Id = 17, Section = "Požiūris į sąskaitas", Supplementary = true,
            Text = "Koks jūsų požiūris į sąskaitas už elektrą ir jų valdymą?",
            Purpose = "Papildomas: rizikos tolerancija lemia, ar dinaminis (biržos), ar fiksuotas tarifas.",
            Options = new[] { PlanFixed, PlanMiddle, PlanOptimize }
        },
        new()
        {
            Id = 30, Section = "Požiūris į sąskaitas", Supplementary = true,
            Text = "Jei vieną žiemos mėnesį sąskaita netikėtai padvigubėtų – kaip tai jums būtų?",
            Purpose = "Papildomas: tikrasis rizikos toleravimas; svarbu prieš rekomenduojant dinaminį tarifą.",
            ShowWhen = new[] { new ShowCondition { QuestionId = 17, HasNone = new[] { PlanFixed } } },
            Options = new[] { DoubleBillUnacceptable, "Nemalonu, bet ištverčiau", DoubleBillOk }
        },

        // ── Pabaiga: laisva informacija ─────────────────────────────────
        new()
        {
            Id = ExtraInfoId, Section = "Papildoma informacija",
            Text = "Ar norėtumėte ką nors pridurti apie save, savo namus ar planus?",
            Purpose = "Laisva informacija, kurios neapėmė klausimai (planuojamas EV, šilumos siurblys, renovacija, " +
                      "darbas pamainomis ir pan.). Visada paskutinis klausimas.",
            IsFreeText = true, AllowCustom = false,
            Options = new[] { NothingToAdd }
        }
    };

    public static int FirstQuestionId => Questions[0].Id;

    public static Question? GetQuestion(int id) => Questions.FirstOrDefault(q => q.Id == id);

    public static bool IsHouseAnswer(string? option) => option is not null && HouseOptions.Contains(option);
}
