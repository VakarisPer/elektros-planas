# elektros-planas

**Hack4Vilnius projektas**

## Tema

Kaip padėti vilniečiams išsirinkti jiems tinkamiausią elektros planą remiantis asmeniniais vartojimo duomenimis?

Fiksuotas ar lankstus tarifas? Ne visi žino, kuris planas jiems tinka geriausiai. Sprendimas, remdamasis vartotojo įpročiais ir duomenimis, padeda pasirinkti tinkamiausią elektros planą (Elektrum, Ignitis, Enefit) bei įvertinti galimas rizikas ir naudą.

## Kaip veikia

1. **AI asistentas** (`/assistant`) — pagrindinis kelias. Agentas (Microsoft Agent Framework per
   OpenRouter) pats parenka kitą informatyviausią klausimą iš klausimų banko, kad su kuo mažiau
   klausimų gautų patikimą išvadą (adaptyvus *tree questioning*). Atsakoma mygtukais arba laisvu tekstu.
2. **Duomenų įkėlimas** (`/upload`) — ESO suvartojimo failas (`.csv` / `.txt`). Analizatorius
   suskaičiuoja mėnesinį vidurkį ir naktinio vartojimo dalį ir automatiškai atsako į susijusius klausimus.
3. **Skaičiuoklė** (`Services/PlanCalculator.cs`) — deterministiškai suskaičiuoja kiekvieno tiekėjo
   fiksuoto, biržos ir dviejų zonų plano metinę kainą (įprasti / pigūs / brangūs metai) ir riziką.
   AI skaičių negalvoja – tik paaiškina skaičiuoklės rezultatą.
4. **Rezultatai** (`/results`) — rekomendacija, planų palyginimas, profilis, `.txt` atsisiuntimas.
5. **Saulės elektrinė ir kaupiklis** (`Services/SolarAdvisor.cs`, `SolarSimulator.cs`) – `tools/solar_calc.py`
   logika perkelta į C# (patikrinta su Python: `dotnet run tools/parity/check.cs`). Jei saulė domina, rezultatuose
   rodoma optimali elektrinė, atsipirkimas, NPV ir visa mėnesio sąskaita kiekvienam plano tipui. Turintiems elektrinę
   (tik ~1 % jų turi kaupiklį) vertinama, ar verta įsirengti kaupiklį. Naudojami ESO valandiniai duomenys (P+/P−),
   jei įkelti. AI agentas skaičiuoklę kviečia įrankiu `simulate_solar`.
6. **Klasikinė anketa** (`/survey`) — tas pats klausimų medis be AI (pėdoje).

Be API rakto asistentas veikia **standartiniu režimu**: klausia pagal taisyklių medį ir rekomendaciją
sudaro skaičiuoklė – demo niekada nesugriūva.

## AI nustatymas (OpenRouter)

```bash
dotnet user-secrets set "OpenRouter:ApiKey" "sk-or-..."
```

Modelis keičiamas `appsettings.json` → `OpenRouter:Model` (reikia modelio su *tool calling*,
pvz. `deepseek/deepseek-v4.1-flash`). `OpenRouter:MaxQuestions` – klausimų limitas.

## Svarbiausi failai

- `Prompts/advisor-system.md` — **sisteminis promptas** (rinkos kontekstas, klausimų parinkimo
  strategija, įrankių protokolas, išvados struktūra). Keičiamas nekeičiant kodo (užtenka perkrauti programą).
- `Prompts/scenarios.md` — testinės personos promptui tikrinti.
- `Data/Questionnaire.cs` — klausimų bankas: tekstai, variantai, `Purpose` (kodėl klausimas svarbus
  agentui) ir `ShowWhen` (kietos medžio taisyklės – nematomo klausimo agentas paklausti negali).
- `Services/ElectricityAdvisorAgent.cs` — agentas ir jo įrankiai: `get_profile`, `ask_question`,
  `record_answer`, `estimate_plans`, `simulate_solar`, `finish`; fallback į taisykles.
- `Services/PlanCalculator.cs` — profilis iš atsakymų ir planų kainų modelis.
- `appsettings.json` → `Tariffs` ir `Solar` — **orientaciniai demo tarifai ir prielaidos**, atnaujinkite pagal
  tiekėjų, montuotojų ir ESO kainas.
- `tools/solar_calc.py` — originali Python skaičiuoklė (etalonas), `tools/parity/` — palyginimas su C#.
- `Components/Pages/` — `Home`, `Assistant`, `Upload`, `Results`, `Survey`.

## Statusas

- [x] Klausimų bankas su medžio sąlygomis (saulė, kaupiklis, išmanusis skaitiklis, EV, rizika)
- [x] Failo įkėlimas ir automatiniai atsakymai
- [x] AI asistentas (Agent Framework + OpenRouter) su adaptyviu klausimų parinkimu
- [x] Planų palyginimas, rizikos ir naudos vertinimas
- [ ] Tikri, automatiškai atnaujinami tiekėjų tarifai ir Nord Pool kainos

## Paleidimas

```bash
dotnet run
```

## Technologijos

- .NET 10 / ASP.NET Core Blazor (Interactive Server)
- Microsoft Agent Framework (`Microsoft.Agents.AI`), `Microsoft.Extensions.AI.OpenAI`
- OpenRouter (OpenAI-suderinamas API)
- Markdig (AI teksto atvaizdavimas)
