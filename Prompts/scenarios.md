# Prompto tikrinimo scenarijai

Po kiekvieno `advisor-system.md` pakeitimo praeikite šias personas `/assistant` puslapyje.
Atsakinėkite kaip persona; jei asistentas klausia ko nors, ko persona nežino – „Nežinau“.

| # | Persona | Pagrindiniai faktai | Laukiama | Klausimų |
|---|---------|---------------------|----------|----------|
| 1 | Studentė mažame bute | Butas iki 50 m², 1 asmuo, iki 150 kWh, centrinis šildymas ir vanduo, nori fiksuotos kainos | Fiksuota; pasako, kad skirtumas mažas | 4–6 |
| 2 | Šeima su EV ir ŠS | Namas 100–200 m², 4 asm., šilumos siurblys valdomas, EV kraunamas naktį, išmanusis skaitiklis, nori optimizuoti, dviguba sąskaita „ok“ | Biržos; paaiškina žiemos riziką | 8–11 |
| 3 | Saulės elektrinė + kaupiklis | Namas, 10 kW saulė, kaupimas tinkle, kaupiklis valdomas pagal biržą | Biržos arba dviejų zonų; būtinai klausia Q26, Q27, Q28 | 8–12 |
| 4 | Senjoras su elektriniu šildymu | Butas 50–80 m², 1 asm., elektrinis šildymas, namie visą dieną, griežtas biudžetas | Fiksuota arba dviejų zonų; perspėja apie biržos riziką žiemą | 6–9 |
| 5 | IT laisvai samdomas | Butas, dirba iš namų, NAS + žaidimų PC vakarais, išmanusis skaitiklis, „galbūt“ keistų rutiną | Dviejų zonų arba fiksuota; nepataria biržos dėl vakarinio vartojimo | 7–10 |
| 6 | Saulė be kaupiklio | Namas, 8 kW saulė, kaupimas tinkle, kaupiklio nėra | Būtinai paklausia Q19; kaupiklis neverta (tinklas = baterija) | 6–9 |
| 7 | Domina saulė | Namas 100–200 m², šilumos siurblys, virš 300 kWh, saulės neturi, bet domina | Kviečia simulate_solar; rezultatuose saulės sekcija | 6–9 |

Tikrinti:
- Ar neklausia to, kas jau žinoma (pvz. Q3 po failo įkėlimo)?
- Ar `intro` paaiškina, kodėl klausiama, ir nekartoja klausimo?
- Ar išvadoje skaičiai sutampa su rezultatų puslapio kortelėmis?
- Ar laisvas tekstas („turiu Teslą, kraunu naktį“) užfiksuojamas be pakartotinio klausimo?
