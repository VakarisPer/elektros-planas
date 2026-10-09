# elektros-planas

**Hack4Vilnius projektas**

> ⚠️ Tai inicijuojantis (init) repo. Čia kol kas tik projekto karkasas ir aprašymas — veikiančią versiją įkelsime vėliau.

## Tema

Kaip padėti vilniečiams išsirinkti jiems tinkamiausią elektros planą remiantis asmeniniais vartojimo duomenimis?

Fiksuotas ar lankstus tarifas? Ne visi žino, kuris planas jiems tinka geriausiai. Sprendimas, remdamasis vartotojo įpročiais ir duomenimis, padės pasirinkti tinkamiausią elektros planą bei įvertinti galimas rizikas ir naudą.

## Statusas

- [x] Projekto karkasas (demo puslapiai išvalyti, liko tik titulinis)
- [ ] Vartojimo duomenų įkėlimas
- [ ] Planų palyginimo logika
- [ ] Rizikos ir naudos vertinimas
- [ ] Veikianti versija

## Struktūra

- `Components/Pages/Home.razor` — titulinis puslapis
- `Models/` — projekto modeliai
- `Services/` — verslo logika (planų palyginimas, rizikos vertinimas)

## Paleidimas

```bash
dotnet run
```

## Technologijos

- .NET / ASP.NET Core (Blazor) — `net10.0`
