# Saulės skaičiuoklės parity patikrinimas

`Services/SolarSimulator.cs` yra `tools/solar_calc.py` C# portas. Šis patikrinimas paleidžia abu
su tais pačiais atvejais (`cases.json`) ir lygina rezultatus: geriausią konfigūraciją, NPV,
1-ųjų metų sutaupymą, gamybą (±1 %) ir atsipirkimą (±0,15 m.).

Paleisti iš projekto aplanko (reikia `python` PATH'e):

```bash
dotnet run tools/parity/check.cs
```

Tikėtina išvestis baigiasi `PARITY OK`. Pakeitus `solar_calc.py` arba `SolarSimulator.cs`,
patikrinimą paleiskite iš naujo.
