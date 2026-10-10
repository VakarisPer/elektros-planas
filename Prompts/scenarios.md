# Prompto tikrinimo scenarijai

Po kiekvieno `advisor-system.md` pakeitimo praeikite šias personas `/assistant` puslapyje.
Atsakinėkite kaip persona; jei asistentas klausia ko nors, ko persona nežino – „Nežinau“.
Klausimai turi eiti pagal schemą: būstas → asmenys → prietaisai (+ EV namui) → skaitiklis →
(namui) saulė / kaupiklis → galima apkrova → kas domina; papildomi – šildymas, požiūris į sąskaitas.

| # | Persona | Pagrindiniai faktai | Laukiama |
|---|---------|---------------------|----------|
| 1 | Studentė mažame bute | Butas iki 50 m², 1 asmuo, nėra prietaisų, paprastas skaitiklis, nori fiksuotos kainos | Saulės klausimo NĖRA; 1 arba 2 zonos; fiksuota; skirtumas mažas |
| 2 | Šeima su EV ir ŠS | Namas 100–200 m², 4 asm., šilumos siurblys, EV kraunamas namuose, išmanusis skaitiklis, saulė nedomina, nori optimizuoti | Biržos arba zonų; paaiškina žiemos riziką |
| 3 | Domina saulė ir kaupiklis | Namas, paprastas skaitiklis, saulė domina, apkrova ≥ 5 kW, domina abu | Pasako apie nemokamą ESO skaitiklio keitimą; įranga + APVA + atsipirkimas; kaupiklis tik su dinaminiu tarifu |
| 4 | Maža galima apkrova | Namas, saulė domina, ESO leidžia < 5 kW | Q34 neklausiamas; vertinamas tik kaupiklis, tik birža |
| 5 | Jau turi elektrinę | Namas, ESO failas su P− (arba „jau turiu“), kaupiklio nėra | Klausia Q26, Q19; investicijos neskaičiuoja; pigiausias pasaugojimo būdas; kaupiklis – tik su dinaminiu |

Tikrinti:
- Ar schemos klausimai užduodami iš eilės ir nekartojami (pvz. skaitiklis po ESO failo įkėlimo)?
- Ar `intro` paaiškina, kodėl klausiama, ir nekartoja klausimo?
- Ar išvadoje skaičiai sutampa su rezultatų puslapiu (galutinis pasiūlymas, sutaupymas)?
- Ar laisvas tekstas („turiu Teslą, kraunu namuose“) užfiksuojamas be pakartotinio klausimo?
