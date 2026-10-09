# elektros-planas

**Hack4Vilnius projektas**

> ⚠️ Tai dar ankstyva versija. Čia jau veikia apklausos vedlys su medžiu (tree questioning),
> failo įkėlimas ir atsakymų išsaugojimas; planų palyginimo logika bus pridėta vėliau.

## Tema

Kaip padėti vilniečiams išsirinkti jiems tinkamiausią elektros planą remiantis asmeniniais vartojimo duomenimis?

Fiksuotas ar lankstus tarifas? Ne visi žino, kuris planas jiems tinka geriausiai. Sprendimas, remdamasis vartotojo įpročiais ir duomenimis, padės pasirinkti tinkamiausią elektros planą bei įvertinti galimas rizikas ir naudą.

## Kaip veikia

1. **Įkėlimas** (`/upload`) — galima įkelti elektros suvartojimo failą (`.csv` / `.txt`).
   Taisyklėmis paremtas analizatorius nuskaito datą/laiką ir kWh, suskaičiuoja mėnesinį
   vidurkį bei naktinio vartojimo dalį ir **automatiškai atsako** su vartojimu susijusius
   klausimus (juos galima pataisyti).
2. **Apklausa** (`/survey/1` … `/survey/{id}`) — **vienas klausimas = vienas puslapis**.
   Klausimai sudėlioti į medį: neaktuolūs klausimai praleidžiami pagal ankstesnius
   atsakymus (žr. `Data/Questionnaire.cs`, laukas `ShowWhen`).
3. **Rezultatai** (`/results`) — atsakymų suvestinė + mygtukas išsaugoti `.txt`.

## Apklausos medis (trumpai)

- Visada: Q1–Q7, Q8, Q9, Q10
- `Q18` (EV krovimo valdymas) — tik jei Q7 = „Taip, kraunu namuose“
- `Q19` (elektros kaupiklis) — tik jei turi/planuoja elektrinę (Q8)
- `Q11` (gaming PC) — praleidžiamas, jei Q10 pažymėta „Kriptovaliutų kasimo įranga“
- `Q17` (požiūris į sąskaitas) — vartai lankstumo blokui
- `Q15`, `Q16` — tik jei Q17 ≠ „Noriu fiksuotos, stabilios kainos“
- `Q12` — tik jei Q17 ≠ „…fiksuotos…“ ir Q16 ≠ „Ne, nenoriu keisti rutinos“
- `Q13` — tik jei Q17 = „tarpinis“ arba „optimizuoti“

Visos taisyklės — viename faile `Data/Questionnaire.cs`.

## Statusas

- [x] Projekto karkasas
- [x] Apklausos vedlys su medžiu (vienas klausimas = vienas puslapis)
- [x] Failo įkėlimas ir automatinių atsakymų generavimas
- [x] Atsakymų išsaugojimas į `.txt` (`data/private/submissions/`)
- [ ] Planų palyginimo logika
- [ ] Rizikos ir naudos vertinimas

## Struktūra

- `Components/Pages/Home.razor` — titulinis
- `Components/Pages/Upload.razor` — failo įkėlimas (`/upload`)
- `Components/Pages/Survey.razor` — vedlys (`/survey/{id}`)
- `Components/Pages/Results.razor` — suvestinė (`/results`)
- `Components/Survey/` — `QuestionCard`, `ProgressBar`
- `Data/Questionnaire.cs` — klausimai, pasirinkimai ir medžio sąlygos
- `Models/` — `Question`, `Answer`, `ShowCondition`, `ConsumptionAnalysis`, `SurveySnapshot`
- `Services/QuestionnaireState.cs` — būsena, medžio ėjimas, `localStorage`
- `Services/ConsumptionAnalyzer.cs` — suvartojimo failo analizė
- `Services/SubmissionWriter.cs` — `.txt` failo rašymas

## Paleidimas

```bash
dotnet run
```

## Technologijos

- .NET / ASP.NET Core (Blazor) — `net10.0`
