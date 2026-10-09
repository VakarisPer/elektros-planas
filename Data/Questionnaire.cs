using vamos.Models;

namespace vamos.Data;

/// <summary>
/// Single source of truth for the survey structure and demo options.
/// Replace the option strings here to change the questionnaire without touching the UI.
/// </summary>
public static class Questionnaire
{
    public const int TotalSteps = 5;

    // Shared option labels (kept as constants to avoid typos in "exclusive" configuration).
    private const string None = "Neturiu";
    private const string NoneOrNotPlanned = "Neturiu ir neplanuoju";

    public static IReadOnlyList<SurveyStep> Steps { get; } = new List<SurveyStep>
    {
        new()
        {
            Number = 1,
            Title = "Bazinė informacija",
            Questions = new List<Question>
            {
                new()
                {
                    Id = 1, Step = 1,
                    Text = "Koks jūsų būsto tipas ir apytikslė kvadratūra?",
                    Options = new[]
                    {
                        "Butas iki 50 m²",
                        "Butas 50–80 m²",
                        "Butas virš 80 m²",
                        "Namas iki 100 m²",
                        "Namas 100–200 m²",
                        "Namas virš 200 m²"
                    }
                },
                new()
                {
                    Id = 2, Step = 1,
                    Text = "Kiek asmenų nuolat gyvena jūsų namuose?",
                    Options = new[] { "1", "2", "3", "4", "5 ar daugiau" }
                },
                new()
                {
                    Id = 3, Step = 1,
                    Text = "Koks apytikslis jūsų vidutinis mėnesinis elektros suvartojimas?",
                    Options = new[] { "Iki 150 kWh", "150–300 kWh", "Virš 300 kWh", "Nežinau" }
                }
            }
        },
        new()
        {
            Number = 2,
            Title = "Didžioji infrastruktūra ir gamyba",
            Questions = new List<Question>
            {
                new()
                {
                    Id = 4, Step = 2,
                    Text = "Kokia jūsų namų šildymo sistema?",
                    Options = new[]
                    {
                        "Šilumos siurblys",
                        "Gamtinės dujos",
                        "Centrinis šildymas",
                        "Elektrinis šildymas (radiatoriai, grindys)",
                        "Kietasis kuras (malkos, granulės)",
                        "Nėra atskiro šildymo"
                    }
                },
                new()
                {
                    Id = 5, Step = 2,
                    Text = "Kaip ruošiate karštą vandenį?",
                    Options = new[]
                    {
                        "Elektrinis boileris",
                        "Šilumos siurblys",
                        "Centrinis (šilumos tinklai)",
                        "Dujinis katilas"
                    }
                },
                new()
                {
                    Id = 6, Step = 2,
                    Text = "Ar namuose įdiegta rekuperacinė vėdinimo arba kondicionavimo sistema?",
                    IsMultiSelect = true,
                    ExclusiveOption = None,
                    Options = new[] { "Rekuperacinė vėdinimo sistema", "Kondicionierius", None }
                },
                new()
                {
                    Id = 7, Step = 2,
                    Text = "Ar turite (arba planuojate įsigyti) elektromobilį, kurį kraunate namuose?",
                    Options = new[]
                    {
                        "Taip, kraunu namuose",
                        "Taip, bet kraunu ne namuose",
                        "Ne, bet planuoju įsigyti",
                        "Ne"
                    }
                },
                new()
                {
                    Id = 8, Step = 2,
                    Text = "Ar turite nuosavą saulės/vėjo elektrinę arba esate įsigiję nutolusio parko dalį?",
                    IsMultiSelect = true,
                    ExclusiveOption = NoneOrNotPlanned,
                    Options = new[]
                    {
                        "Nuosava saulės/vėjo elektrinė",
                        "Nutolusio saulės/vėjo parko dalis",
                        NoneOrNotPlanned
                    }
                }
            }
        },
        new()
        {
            Number = 3,
            Title = "Specifiniai įrenginiai ir išmanumas",
            Questions = new List<Question>
            {
                new()
                {
                    Id = 9, Step = 3,
                    Text = "Ar turite daug elektros reikalaujančių pramogų įrenginių?",
                    IsMultiSelect = true,
                    ExclusiveOption = None,
                    Options = new[] { "Elektrinė pirtis", "Šildomas lauko baseinas", "Sūkurinė vonia", None }
                },
                new()
                {
                    Id = 10, Step = 3,
                    Text = "Ar turite nuolat veikiančios kompiuterinės technikos?",
                    IsMultiSelect = true,
                    ExclusiveOption = None,
                    Options = new[] { "Namų serveris / NAS", "Kriptovaliutų kasimo įranga", None }
                },
                new()
                {
                    Id = 11, Step = 3,
                    Text = "Ar dažnai ir ilgai naudojate galingus kompiuterius žaidimams ar vaizdo redagavimui?",
                    Options = new[]
                    {
                        "Taip, kasdien kelias valandas",
                        "Taip, kelis kartus per savaitę",
                        "Retai",
                        "Nenaudoju"
                    }
                },
                new()
                {
                    Id = 12, Step = 3,
                    Text = "Ar jūsų stambioji buitinė technika (skalbyklė, džiovyklė, indaplovė) turi atidėto starto funkciją?",
                    Options = new[] { "Taip, dauguma prietaisų", "Taip, kai kurie", "Ne", "Nežinau" }
                },
                new()
                {
                    Id = 13, Step = 3,
                    Text = "Ar naudojate išmaniuosius namų sprendimus, kuriais galima automatizuoti elektros vartojimą?",
                    IsMultiSelect = true,
                    ExclusiveOption = "Nenaudoju",
                    Options = new[]
                    {
                        "Išmaniosios rozetės",
                        "Automatinis šilumos siurblio valdymas",
                        "Nenaudoju"
                    }
                }
            }
        },
        new()
        {
            Number = 4,
            Title = "Gyvenimo ritmas ir įpročiai",
            Questions = new List<Question>
            {
                new()
                {
                    Id = 14, Step = 4,
                    Text = "Koks jūsų ir šeimos įprastas dienos ritmas darbo dienomis?",
                    Options = new[]
                    {
                        "Namuose nieko nėra 8–17 val.",
                        "Dirbu iš namų",
                        "Mišrus (keliomis dienomis iš namų)",
                        "Namuose būna beveik visą dieną"
                    }
                },
                new()
                {
                    Id = 15, Step = 4,
                    Text = "Kada dažniausiai atliekate daugiausiai elektros reikalaujančius buities darbus?",
                    Options = new[]
                    {
                        "Ryte (6–9 val.)",
                        "Dieną (9–17 val.)",
                        "Vakare (17–22 val.)",
                        "Naktį (22–6 val.)",
                        "Savaitgaliais"
                    }
                },
                new()
                {
                    Id = 16, Step = 4,
                    Text = "Jei naktį arba savaitgalį elektra būtų kelis kartus pigesnė, ar pakeistumėte rutiną ir atidėtumėte šiuos darbus?",
                    Options = new[]
                    {
                        "Taip, tikrai pakeisčiau rutiną",
                        "Galbūt, išbandyčiau",
                        "Ne, nenoriu keisti rutinos"
                    }
                }
            }
        },
        new()
        {
            Number = 5,
            Title = "Lūkesčiai",
            Questions = new List<Question>
            {
                new()
                {
                    Id = 17, Step = 5,
                    Text = "Koks jūsų požiūris į sąskaitas už elektrą ir jų valdymą?",
                    Options = new[]
                    {
                        "Noriu fiksuotos, stabilios kainos – nenoriu sekti biržos",
                        "Esu linkęs prisiimti riziką ir optimizuoti išlaidas"
                    }
                }
            }
        }
    };

    public static SurveyStep? GetStep(int number) => Steps.FirstOrDefault(s => s.Number == number);

    public static Question? GetQuestion(int id) =>
        Steps.SelectMany(s => s.Questions).FirstOrDefault(q => q.Id == id);
}
