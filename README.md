# elektros-planas

**Hack4Vilnius projektas**

> ⚠️ Tai inicijuojantis (init) repo. Čia kol kas tik projekto karkasas ir aprašymas — veikiančią versiją įkelsime vėliau.

## Tema

Kaip padėti vilniečiams išsirinkti jiems tinkamiausią elektros planą remiantis asmeniniais vartojimo duomenimis?

Fiksuotas ar lankstus tarifas? Ne visi žino, kuris planas jiems tinka geriausiai. Sprendimas, remdamasis vartotojo įpročiais ir duomenimis, padės pasirinkti tinkamiausią elektros planą bei įvertinti galimas rizikas ir naudą.

## Statusas

- [x] Projekto karkasas (demo puslapiai išvalyti, liko tik titulinis)
- [x] Apklausos vedlys (5 žingsniai, /survey/1–5) su atsakymų išsaugojimu
- [ ] Vartojimo duomenų įkėlimas
- [ ] Planų palyginimo logika
- [ ] Rizikos ir naudos vertinimas
- [ ] Veikianti versija

## Struktūra

- `Components/Pages/Home.razor` — titulinis puslapis
- `Components/Pages/Survey.razor` — apklausos vedlys (`/survey/1`–`/survey/5`)
- `Components/Pages/Results.razor` — atsakymų suvestinė (`/results`)
- `Components/Survey/` — vedlio komponentai (`QuestionCard`, `ProgressBar`)
- `Data/Questionnaire.cs` — visi klausimai ir pasirinkimai (vienoje vietoje)
- `Models/` — projekto modeliai (`Question`, `Answer`, `SurveyStep`, `SurveySnapshot`)
- `Services/QuestionnaireState.cs` — atsakymų būsena ir saugojimas (localStorage)

## Atsakymų saugojimas

Apklausos atsakymai saugomi naršyklės `localStorage` (raktas `elektros-planai.survey.v1`)
per `ProtectedLocalStorage`, todėl išlieka persikrovus puslapį. Mygtukas „Pildyti iš naujo“
rezultatų puslapyje išvalo išsaugotus atsakymus.

## Paleidimas

```bash
dotnet run
```

## Technologijos

- .NET / ASP.NET Core (Blazor) — `net10.0`
