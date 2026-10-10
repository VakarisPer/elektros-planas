# Saulės skaičiuoklės parity patikrinimas

`Services/SolarSimulator.cs` saulės gamybos ir investicijų dalis yra `tools/solar_calc.py` C# portas.
Šis patikrinimas paleidžia abu su tais pačiais atvejais (`cases.json`) ir lygina geriausią elektrinę
**be kaupiklio**: galią, NPV, 1-ųjų metų sutaupymą, gamybą (±1 %) ir atsipirkimą (±0,15 m.).

Kaupiklis nebelyginamas: jis dabar skaičiuojamas pagal projekto dokumentaciją
(`k = min(p, galia·Δt, (talpa − SoC)/η)`, η taikomas krovimui, be DoD ir be krovimo iš tinklo),
o ne pagal Python modelį. Dokumentacijos pavyzdžiai tikrinami `tools/formula-tests/check.cs`.

Paleisti iš projekto aplanko (reikia `python` PATH'e):

```bash
dotnet run --file tools/parity/check.cs
```

Tikėtina išvestis baigiasi `PARITY OK`. Pakeitus `solar_calc.py` arba `SolarSimulator.cs`,
patikrinimą paleiskite iš naujo.
