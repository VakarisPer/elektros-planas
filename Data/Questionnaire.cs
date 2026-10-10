using vamos.Models;

namespace vamos.Data;

/// <summary>
/// Single source of truth for the question bank.
/// <para>
/// The list is in "funnel" order (būstas → infrastruktūra → įrenginiai → požiūris → įpročiai).
/// The classic wizard walks it top to bottom; the AI advisor picks questions from it by id.
/// <see cref="Question.ShowWhen"/> conditions are hard rules for both: a hidden question is never asked.
/// </para>
/// </summary>
public static class Questionnaire
{
    private const string None = "Neturiu";

    // Option labels reused in tree conditions and in PlanCalculator (kept as constants to avoid typos).
    public const string PlanFixed = "Noriu fiksuotos, stabilios kainos – nenoriu sekti biržos";
    public const string PlanMiddle = "Norėčiau sutaupyti, bet nenoriu daug keisti įpročių";
    public const string PlanOptimize = "Esu linkęs prisiimti riziką ir aktyviai optimizuoti išlaidas";

    public const string MonthlyLow = "Iki 150 kWh";
    public const string MonthlyMid = "150–300 kWh";
    public const string MonthlyHigh = "Virš 300 kWh";

    public const string HeatPump = "Šilumos siurblys";
    public const string ElectricHeating = "Elektrinis šildymas (radiatoriai, grindys)";
    public const string ElectricBoiler = "Elektrinis boileris";

    public const string EvChargeAtHome = "Taip, kraunu namuose";
    public const string EvNightOnly = "Taip, kraunu tik naktį";
    public const string EvCanDelay = "Taip, galiu atidėti";

    public const string OwnSolar = "Nuosava saulės/vėjo elektrinė";
    public const string RemoteSolar = "Nutolusio saulės/vėjo parko dalis";
    public const string SolarInterested = "Neturiu, bet domina (jei atsipirktų)";
    public const string SolarNo = "Neturiu ir nedomina";

    public const string BatteryYes = "Taip, turiu";
    public const string BatteryConsidering = "Ne, bet svarstau";
    public const string BatteryPriceControlled = "Taip, automatiškai pagal biržos kainą";

    public const string SmartMeterYes = "Taip, ESO išmanusis skaitiklis jau įrengtas";

    public const string CryptoMining = "Kriptovaliutų kasimo įranga";

    public const string RoutineYes = "Taip, tikrai pakeisčiau rutiną";
    public const string RoutineMaybe = "Galbūt, išbandyčiau";
    public const string NoRoutineChange = "Ne, nenoriu keisti rutinos";

    public const string TimersMost = "Taip, dauguma prietaisų";
    public const string TimersSome = "Taip, kai kurie";
    public const string NoSmartHome = "Nenaudoju";

    public const string DoubleBillUnacceptable = "Būtų nepriimtina – biudžetas griežtas";
    public const string DoubleBillOk = "Gerai, jei per metus vis tiek sutaupau";

    /// <summary>All questions in funnel order.</summary>
    public static IReadOnlyList<Question> Questions { get; } = new List<Question>
    {
        // ── 1 blokas: Būstas ir vartojimas ──────────────────────────────
        new()
        {
            Id = 1, Section = "Būstas ir vartojimas",
            Text = "Koks jūsų būsto tipas ir apytikslė kvadratūra?",
            Purpose = "Vartojimo masto proxy, jei nežinomas kWh; namas dažniau turi ŠS, saulę, EV.",
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
            Purpose = "Vartojimo masto proxy; daugiau žmonių – daugiau buities darbų, kuriuos galima perkelti.",
            Options = new[] { "1", "2", "3", "4", "5 ar daugiau" }
        },
        new()
        {
            Id = 3, Section = "Būstas ir vartojimas",
            Text = "Koks apytikslis jūsų vidutinis mėnesinis elektros suvartojimas?",
            Purpose = "Svarbiausias skaičius: nuo jo priklauso, kiek eurų per metus skiriasi planai.",
            Options = new[] { MonthlyLow, MonthlyMid, MonthlyHigh, "Nežinau" }
        },
        new()
        {
            Id = 20, Section = "Būstas ir vartojimas",
            Text = "Ar turite ESO išmanųjį elektros skaitiklį?",
            Purpose = "Be išmaniojo skaitiklio biržos plano valandinė nauda (vartojimo perkėlimas) beveik neišnaudojama.",
            Options = new[] { SmartMeterYes, "Ne, senas skaitiklis", "Nežinau" }
        },
        new()
        {
            Id = 21, Section = "Būstas ir vartojimas",
            Text = "Koks jūsų dabartinis elektros tiekėjas ir planas?",
            Purpose = "Kontekstas palyginimui (ar keisti verta); mažai keičia rekomenduojamą tipą.",
            Options = new[]
            {
                "Fiksuota kaina", "Biržos (dinaminė) kaina", "Dviejų laiko zonų planas", "Nežinau"
            }
        },

        // ── 2 blokas: Infrastruktūra ir gamyba ──────────────────────────
        new()
        {
            Id = 4, Section = "Infrastruktūra ir gamyba",
            Text = "Kokia jūsų namų šildymo sistema?",
            Purpose = "Elektrinis šildymas/ŠS – didžiausias vartotojas žiemą, kai biržos kainos aukščiausios.",
            Options = new[]
            {
                HeatPump, "Gamtinės dujos", "Centrinis šildymas",
                ElectricHeating, "Kietasis kuras (malkos, granulės)", "Nėra atskiro šildymo"
            }
        },
        new()
        {
            Id = 23, Section = "Infrastruktūra ir gamyba",
            Text = "Ar šilumos siurblį galima valdyti pagal laiką ar kainą (pvz. per programėlę)?",
            Purpose = "Valdomas ŠS gali pašildyti namą pigiomis valandomis – didelė biržos plano nauda.",
            ShowWhen = new[] { new ShowCondition { QuestionId = 4, HasAny = new[] { HeatPump } } },
            Options = new[] { "Taip, jau valdau automatiškai", "Taip, bet nenaudoju", "Ne / nežinau" }
        },
        new()
        {
            Id = 5, Section = "Infrastruktūra ir gamyba",
            Text = "Kaip ruošiate karštą vandenį?",
            Purpose = "Elektrinis boileris su laikmačiu – lengviausiai naktį perkeliamas vartojimas.",
            Options = new[]
            {
                ElectricBoiler, HeatPump, "Centrinis (šilumos tinklai)", "Dujinis katilas"
            }
        },
        new()
        {
            Id = 24, Section = "Infrastruktūra ir gamyba",
            Text = "Kuo gaminate maistą?",
            Purpose = "Elektrinė viryklė/orkaitė didina vakarinį (brangiausią) vartojimą.",
            Options = new[] { "Indukcinė / elektrinė viryklė", "Dujinė viryklė" }
        },
        new()
        {
            Id = 6, Section = "Infrastruktūra ir gamyba",
            Text = "Ar namuose įdiegta rekuperacinė vėdinimo arba kondicionavimo sistema?",
            Purpose = "Nuolatinis bazinis vartojimas; kondicionierius – vasaros dienos (pigios saulėtos valandos).",
            IsMultiSelect = true, ExclusiveOption = None,
            Options = new[] { "Rekuperacinė vėdinimo sistema", "Kondicionierius", None }
        },
        new()
        {
            Id = 7, Section = "Infrastruktūra ir gamyba",
            Text = "Ar turite (arba planuojate įsigyti) elektromobilį, kurį kraunate namuose?",
            Purpose = "EV krovimas namuose – didelis ir lengvai perkeliamas į naktį vartojimas.",
            Options = new[]
            {
                EvChargeAtHome, "Taip, bet kraunu ne namuose",
                "Ne, bet planuoju įsigyti", "Ne"
            }
        },
        new()
        {
            Id = 18, Section = "Infrastruktūra ir gamyba",
            Text = "Ar galite valdyti elektromobilio krovimo laiką (pvz., krauti naktį)?",
            Purpose = "Ar EV vartojimą realiai galima perkelti į pigias valandas.",
            ShowWhen = new[] { new ShowCondition { QuestionId = 7, HasAny = new[] { EvChargeAtHome } } },
            Options = new[] { EvNightOnly, EvCanDelay, "Ne, kraunu kada tenka" }
        },
        new()
        {
            Id = 25, Section = "Infrastruktūra ir gamyba",
            Text = "Kiek kilometrų per mėnesį apytiksliai nuvažiuojate elektromobiliu?",
            Purpose = "EV krovimo kWh kiekis – kiek eurų galima sutaupyti kraunant pigiai.",
            ShowWhen = new[] { new ShowCondition { QuestionId = 7, HasAny = new[] { EvChargeAtHome } } },
            Options = new[] { "Iki 500 km", "500–1500 km", "Virš 1500 km" }
        },
        new()
        {
            Id = 8, Section = "Infrastruktūra ir gamyba",
            Text = "Ar turite saulės elektrinę (ar nutolusio parko dalį)? Jei ne – ar domintų?",
            Purpose = "Gaminantis perka mažai ir daugiausia žiemą – keičia planų palyginimą; „domina“ įjungia saulės elektrinės atsipirkimo skaičiavimą.",
            Options = new[] { OwnSolar, RemoteSolar, SolarInterested, SolarNo }
        },
        new()
        {
            Id = 26, Section = "Infrastruktūra ir gamyba",
            Text = "Kokia jūsų elektrinės (ar parko dalies) galia?",
            Purpose = "Kiek metinio vartojimo padengia sava gamyba.",
            ShowWhen = new[] { new ShowCondition { QuestionId = 8, HasAny = new[] { OwnSolar, RemoteSolar } } },
            Options = new[] { "Iki 5 kW", "5–10 kW", "Virš 10 kW", "Nežinau" }
        },
        new()
        {
            Id = 27, Section = "Infrastruktūra ir gamyba",
            Text = "Kokia schema naudojatės kaip gaminantis vartotojas?",
            Purpose = "Kaupimas tinkle išlygina sezoną; pertekliaus pardavimas daro biržos kainą svarbesnę.",
            ShowWhen = new[] { new ShowCondition { QuestionId = 8, HasAny = new[] { OwnSolar, RemoteSolar } } },
            Options = new[] { "Kaupimas ESO tinkle (grąžinu žiemą)", "Perteklių parduodu", "Nežinau" }
        },
        new()
        {
            Id = 19, Section = "Infrastruktūra ir gamyba",
            Text = "Ar prie saulės elektrinės turite elektros kaupiklį (namų bateriją)?",
            Purpose = "Tik ~1 % gaminančių turi kaupiklį – įvertinsime, ar jį verta įsirengti; su biržos planu kaupiklis gali pirkti pigiai ir vartoti brangiu metu.",
            ShowWhen = new[] { new ShowCondition { QuestionId = 8, HasAny = new[] { OwnSolar } } },
            Options = new[] { BatteryYes, BatteryConsidering, "Ne" }
        },
        new()
        {
            Id = 28, Section = "Infrastruktūra ir gamyba",
            Text = "Kaip valdomas jūsų kaupiklis?",
            Purpose = "Ar kaupiklis išnaudoja biržos kainų skirtumus (arbitražas).",
            ShowWhen = new[] { new ShowCondition { QuestionId = 19, HasAny = new[] { BatteryYes } } },
            Options = new[]
            {
                BatteryPriceControlled, "Taip, tik saulės energijai kaupti", "Nežinau"
            }
        },

        // ── 3 blokas: Įrenginiai ────────────────────────────────────────
        new()
        {
            Id = 9, Section = "Įrenginiai",
            Text = "Ar turite daug elektros reikalaujančių pramogų įrenginių?",
            Purpose = "Pirtis/baseinas – dideli, dažnai vakariniai vartotojai.",
            IsMultiSelect = true, ExclusiveOption = None,
            Options = new[] { "Elektrinė pirtis", "Šildomas lauko baseinas", "Sūkurinė vonia", None }
        },
        new()
        {
            Id = 10, Section = "Įrenginiai",
            Text = "Ar turite nuolat veikiančios kompiuterinės technikos?",
            Purpose = "24/7 bazinis vartojimas – didina kWh, bet nepakeičiamas į naktį.",
            IsMultiSelect = true, ExclusiveOption = None,
            Options = new[] { "Namų serveris / NAS", CryptoMining, None }
        },
        new()
        {
            Id = 11, Section = "Įrenginiai",
            Text = "Ar dažnai ir ilgai naudojate galingus kompiuterius žaidimams ar vaizdo redagavimui?",
            Purpose = "Vakarinis vartojimas brangiausiomis valandomis.",
            ShowWhen = new[] { new ShowCondition { QuestionId = 10, HasNone = new[] { CryptoMining } } },
            Options = new[]
            {
                "Taip, kasdien kelias valandas", "Taip, kelis kartus per savaitę",
                "Retai", "Nenaudoju"
            }
        },

        // ── 4 blokas: Požiūris į sąskaitas ──────────────────────────────
        new()
        {
            Id = 17, Section = "Požiūris į sąskaitas",
            Text = "Koks jūsų požiūris į sąskaitas už elektrą ir jų valdymą?",
            Purpose = "Rizikos tolerancija – lemia, ar pigesnis vidutiniškai, bet svyruojantis planas tinka.",
            Options = new[] { PlanFixed, PlanMiddle, PlanOptimize }
        },
        new()
        {
            Id = 30, Section = "Požiūris į sąskaitas",
            Text = "Jei vieną žiemos mėnesį sąskaita netikėtai padvigubėtų – kaip tai jums būtų?",
            Purpose = "Tikrasis rizikos toleravimas (ne deklaruojamas); kritinis prieš rekomenduojant biržą.",
            ShowWhen = new[] { new ShowCondition { QuestionId = 17, HasNone = new[] { PlanFixed } } },
            Options = new[] { DoubleBillUnacceptable, "Nemalonu, bet ištverčiau", DoubleBillOk }
        },

        // ── 5 blokas: Gyvenimo ritmas ir lankstumas ─────────────────────
        new()
        {
            Id = 14, Section = "Gyvenimo ritmas",
            Text = "Koks jūsų ir šeimos įprastas dienos ritmas darbo dienomis?",
            Purpose = "Kada vartojama: dieną (pigesnės saulėtos valandos) ar tik vakare (brangiausios).",
            Options = new[]
            {
                "Namuose nieko nėra 8–17 val.", "Dirbu iš namų",
                "Mišrus (keliomis dienomis iš namų)", "Namuose būna beveik visą dieną"
            }
        },
        new()
        {
            Id = 15, Section = "Gyvenimo ritmas",
            Text = "Kada dažniausiai atliekate daugiausiai elektros reikalaujančius buities darbus?",
            Purpose = "Dabartinis vartojimo profilis: vakaras = brangu biržoje, naktis = pigu.",
            ShowWhen = new[] { new ShowCondition { QuestionId = 17, HasNone = new[] { PlanFixed } } },
            Options = new[]
            {
                "Ryte (6–9 val.)", "Dieną (9–17 val.)", "Vakare (17–22 val.)",
                "Naktį (22–6 val.)", "Savaitgaliais"
            }
        },
        new()
        {
            Id = 16, Section = "Gyvenimo ritmas",
            Text = "Jei naktį arba savaitgalį elektra būtų kelis kartus pigesnė, ar pakeistumėte rutiną?",
            Purpose = "KRITINIS: tiesiogiai matuoja lankstumą – be jo biržos/zonų planų nauda neišnaudojama.",
            ShowWhen = new[] { new ShowCondition { QuestionId = 17, HasNone = new[] { PlanFixed } } },
            Options = new[] { RoutineYes, RoutineMaybe, NoRoutineChange }
        },
        new()
        {
            Id = 12, Section = "Gyvenimo ritmas",
            Text = "Ar jūsų stambioji buitinė technika turi atidėto starto („Delay Timer“) funkciją?",
            Purpose = "Ar perkėlimas į naktį realus be papildomų pastangų.",
            ShowWhen = new[]
            {
                new ShowCondition { QuestionId = 17, HasNone = new[] { PlanFixed } },
                new ShowCondition { QuestionId = 16, HasNone = new[] { NoRoutineChange } }
            },
            Options = new[] { TimersMost, TimersSome, "Ne", "Nežinau" }
        },
        new()
        {
            Id = 13, Section = "Gyvenimo ritmas",
            Text = "Ar naudojate išmaniuosius namų sprendimus elektros vartojimui automatizuoti?",
            Purpose = "Automatika perkelia vartojimą be žmogaus pastangų – mažina biržos riziką.",
            ShowWhen = new[]
            {
                new ShowCondition { QuestionId = 17, HasAny = new[] { PlanMiddle, PlanOptimize } }
            },
            IsMultiSelect = true, ExclusiveOption = NoSmartHome,
            Options = new[] { "Išmaniosios rozetės", "Automatinis šilumos siurblio valdymas", NoSmartHome }
        },
        new()
        {
            Id = 31, Section = "Gyvenimo ritmas",
            Text = "Kiek laiko planuojate gyventi šiame būste?",
            Purpose = "Ilgo termino fiksuota sutartis su nutraukimo mokesčiu netinka, jei greitai kraustysitės.",
            Options = new[] { "Mažiau nei metus", "1–3 metus", "Ilgiau nei 3 metus" }
        }
    };

    public static int FirstQuestionId => Questions[0].Id;

    public static Question? GetQuestion(int id) => Questions.FirstOrDefault(q => q.Id == id);
}
