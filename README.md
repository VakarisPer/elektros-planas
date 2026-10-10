# elektros-planas

**Hack4Vilnius projektas**

## Tema

Kaip padėti vilniečiams išsirinkti jiems tinkamiausią elektros planą remiantis asmeniniais vartojimo duomenimis?

Fiksuotas ar lankstus tarifas? Ne visi žino, kuris planas jiems tinka geriausiai. Sprendimas, remdamasis vartotojo įpročiais ir duomenimis, padeda pasirinkti tinkamiausią elektros planą (Elektrum, Ignitis, Enefit) bei įvertinti galimas rizikas ir naudą.

## Kaip veikia

**Klausimų schema (pagrindas):** duomenų įkėlimas → būsto tipas ir kvadratūra → **butas** (asmenys → prietaisai →
skaitiklis → 3 ESO planai → tarifas → pasiūlymas su sutaupymu) arba **namas** (asmenys → prietaisai + elektromobilis →
skaitiklis → „Ar aktualios saulės panelės arba kaupiklis?“ → ne: planai ir tarifas; taip: galima apkrova ESO
(≥ 5 kW – panelės ir/ar kaupiklis, < 5 kW – tik kaupiklis) → įrangos kaina + APVA → 3 ESO planai → tarifas
(panelės – dinaminis arba fiksuotas, kaupiklis – tik dinaminis) → energijos pasaugojimo būdas → pasiūlymas su
atsipirkimu). Zonos, ESO planai, tarifas ir pasaugojimo būdas skaičiuojami, ne klausiami. Papildomi klausimai:
šildymas, požiūris į sąskaitas, laisva informacija pabaigoje.

1. **AI asistentas** (`/assistant`) — pagrindinis kelias. Agentas (Microsoft Agent Framework per
   OpenRouter) klausia pagal schemą (schemos klausimai tik iš eilės, papildomus gali praleisti), paaiškina
   skaičius ir atsako į laisvus klausimus. Atsakoma mygtukais arba laisvu tekstu.
2. **Duomenų įkėlimas** (`/upload`) — ESO suvartojimo failas (`.csv` / `.txt`). Analizatorius
   suskaičiuoja mėnesinį vidurkį, naktinio vartojimo dalį ir valandinį profilį (mėnuo × darbo diena /
   savaitgalis-šventė × valanda, P+ ir P−) ir automatiškai atsako į susijusius klausimus.
3. **Skaičiuoklė** (`Services/BillEngine.cs`, `PlanCalculator.cs`) — pagal projekto dokumentaciją:
   **Galutinė kaina = ESO dalis + tiekėjo dalis ± saulės elektrinė ± kaupiklis**,
   `kaina(t) = energija(t) + ESO_tarifas(zona(t)) + VIAP + DED`. Sugeneruojami visi deriniai
   ESO planas (Standartinis / Efektyvus / Namai) × zonos (1 / 2 / 4) × tiekėjo planas, atmetami
   neleistini (zonų nesutapimas, 4 zonos be išmaniojo skaitiklio, garantinis tiekimas), kiekvienam
   suskaičiuojama metinė sąskaita valanda po valandos (8 760 val., su šventėmis ir vasaros laiku) ir
   surikiuojama nuo pigiausio. Biržos kainos – tikros LT Nord Pool valandinės kainos. Skaičiuojama ir
   **asmeninė svertinė kaina** Σ(kWh × np) ÷ Σ kWh bei rizikos įspėjimai. AI skaičių negalvoja – tik paaiškina.
4. **Rezultatai** (`/results`) — rekomendacija, geriausias kiekvieno tipo derinys su sąskaitos išskaidymu
   (ESO zonos, pastovūs mokesčiai, energija, VIAP + DED), visi deriniai, profilis, `.txt` atsisiuntimas;
   `/zodynelis` – sąvokų žodynėlis.
5. **Saulės elektrinė ir kaupiklis** (`Services/SolarAdvisor.cs`, `SolarSimulator.cs`, `BatteryArbitrage.cs`) –
   gamyba ir NPV iš `tools/solar_calc.py` (patikrinta su Python). Gaminantiems skaičiuojama **dvipusė apskaita**
   (kWh bankas kas mėnesį nuo balandžio, 4 ESO būdai) ir **grynasis atsiskaitymas** (piniginė iš (np − 0,0247) €/kWh)
   – parenkamas pigiausias. Kaupiklis – pagal dokumentaciją (`k = min(p, galia·Δt, (talpa − SoC)/η)`, η = 0,9);
   be elektrinės – arbitražas (krauti pigiausiomis valandomis, iškrauti brangiausiomis, tik jei
   `brangi − pigi ÷ η > 0`). AI agentas skaičiuoklę kviečia įrankiu `simulate_solar`.
6. **ESO prijungimo galimybės** (`Services/EsoConnectionService.cs`) – pagal 8 skaitmenų objekto numerį (randamas
   automatiškai ESO CSV faile) užklausiamas viešas ESO įrankis „Saulės elektrinės prijungimo galimybės“: įrengta ir
   leistina generuoti galia bei ESO vertinimas. Tiksli įrengta galia ir eksporto ribojimas (pvz. „iki 1 kW“) naudojami
   saulės skaičiavime. Numeris siunčiamas į eso.lt tik paspaudus „Tikrinti ESO“ arba AI įrankiu `check_grid_connection`.
7. **Klasikinė anketa** (`/survey`) — tas pats klausimų medis be AI (pėdoje).

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
- `Data/Questionnaire.cs` — klausimų bankas schemos tvarka: tekstai, variantai, `Purpose`, `ShowWhen` (schemos šakos –
  nematomo klausimo agentas paklausti negali), `Supplementary` (papildomi), `Notes` (sąlyginiai pranešimai, pvz. apie
  išmanųjį skaitiklį ir ESO nuorodą), `ShowEsoCheck` (ESO patikrinimas klausime).
- `Services/ElectricityAdvisorAgent.cs` — agentas ir jo įrankiai: `get_profile`, `ask_question`,
  `record_answer`, `estimate_plans`, `simulate_solar`, `check_grid_connection`, `finish`; fallback į taisykles.
- `Services/PlanCalculator.cs` — profilis iš atsakymų (valandinis importas / eksportas), derinių kainos,
  ESO plano paaiškinimai; `Services/BillEngine.cs` — sąskaitos formulės; `Services/TariffCalendar.cs` —
  laiko zonos, šventės, vasaros laikas.
- `Data/Tariffs/eso-tariffs.json` — ESO tarifai (nuo 2026-01-01), VIAP, DED, gaminančių vartotojų būdai.
- `Data/Tariffs/supplier-plans.json` — **DEMO** tiekėjų planai (laukai kaip dokumentacijoje: `tiekejas`,
  `tipas`, `trukme_men`, `zonos`, `kainos`, `marza`, `men_mokestis`, `su_pvm`, `iskaicia_eso`, `galioja_nuo/iki`).
- `Data/Prices/np-lt.csv` — LT Nord Pool valandinės kainos (2025-10 – 2026-09), atnaujinamos
  `python -X utf8 tools/prices/fetch_np.py 2025-10-01 2026-10-01` (viešas Elering API).
- `appsettings.json` → `Tariffs` (biržos scenarijai, numatytoji LG) ir `Solar` (investicijų prielaidos).
- `tools/formula-tests/check.cs` — dokumentacijos pavyzdžių testai: `dotnet run --file tools/formula-tests/check.cs`.
- `tools/solar_calc.py` — originali Python skaičiuoklė, `tools/parity/` — saulės gamybos palyginimas su C#
  (`dotnet run --file tools/parity/check.cs`).
- `Components/Pages/` — `Home`, `Assistant`, `Upload`, `Results`, `Survey`, `Glossary`.

## Statusas

- [x] Klausimų bankas pagal projekto schemą (butas / namas, skaitiklis, saulė / kaupiklis, galima apkrova, APVA)
- [x] Failo įkėlimas ir automatiniai atsakymai
- [x] AI asistentas (Agent Framework + OpenRouter) su adaptyviu klausimų parinkimu
- [x] Planų palyginimas, rizikos ir naudos vertinimas
- [x] ESO planai ir zonos, tikri ESO tarifai, tikros Nord Pool valandinės kainos
- [x] Dvipusė apskaita (4 būdai), grynasis atsiskaitymas, kaupiklis su ir be elektrinės
- [ ] Tikri, automatiškai atnaujinami tiekėjų tarifai

## Paleidimas

```bash
dotnet run
```

## Technologijos

- .NET 10 / ASP.NET Core Blazor (Interactive Server)
- Microsoft Agent Framework (`Microsoft.Agents.AI`), `Microsoft.Extensions.AI.OpenAI`
- OpenRouter (OpenAI-suderinamas API)
- Markdig (AI teksto atvaizdavimas)
