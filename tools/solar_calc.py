#!/usr/bin/env python3
"""
Saulės elektrinės ir baterijos atsipirkimo skaičiuoklė (Vilnius).

Įvestis: gyvenamosios vietos tipas (butas/namas), metinis suvartojimas,
daugiausiai elektros naudojamos valandos (arba valandiniai duomenys iš CSV).
Išvestis: ar verta saulės elektrinė, ar verta baterija, atsipirkimas ir NPV.

Naudojimas:
  python solar_calc.py --type namas --kwh 6000 --peak-hours 7-9,17-22
  python solar_calc.py --type butas --kwh 2200 --peak-hours 18-22 --json

Visi kainų ir kaštų parametrai yra PAGRINDINĖS PRIELAIDOS: patikrinkite
juos pagal tikras tiekėjų kainas, ESO tarifus ir galiojančias prosumerių taisykles.
"""
import argparse
import csv
import json
import math

HOURS = 8760
MONTH_DAYS = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31]
# Santykinis debesuotumo/giedros dienos veiksnys pagal mėnesį (Vilnius)
MONTH_WEATHER = [0.45, 0.60, 0.75, 0.85, 0.90, 0.90, 0.90, 0.90, 0.80, 0.65, 0.45, 0.40]
LAT = 54.7

# --- laiko lentelės ---
MONTH_OF = []
for m, d in enumerate(MONTH_DAYS):
    MONTH_OF += [m] * (d * 24)


def parse_hours(spec):
    """'7-9,17-22' -> {7,8,17,18,19,20,21}. Intervalas [nuo, iki)."""
    hours = set()
    for part in spec.split(","):
        part = part.strip()
        if not part:
            continue
        a, b = part.split("-")
        a, b = int(a) % 24, int(b) % 24
        h = a
        while h % 24 != b:
            hours.add(h % 24)
            h += 1
    return hours


def build_gen_shape():
    """Normalizuota valandinė saulės gamybos forma (suma = 1)."""
    lat = math.radians(LAT)
    shape = []
    for i in range(HOURS):
        n = i // 24 + 1
        hr = i % 24 + 0.5
        noon = 12.3 + (1.0 if 90 <= n <= 300 else 0.0)  # vasaros laikas
        dec = math.radians(23.45 * math.sin(2 * math.pi * (284 + n) / 365))
        ha = math.radians(15 * (hr - noon))
        s = math.sin(lat) * math.sin(dec) + math.cos(lat) * math.cos(dec) * math.cos(ha)
        shape.append(max(0.0, s) * MONTH_WEATHER[MONTH_OF[i]])
    tot = sum(shape)
    return [x / tot for x in shape]


def build_load(kwh, peak_hours, peak_weight=2.5, winter_boost=0.15):
    """Valandinis vartojimo profilis iš metinės sumos ir pikinių valandų."""
    w = []
    for i in range(HOURS):
        hod = i % 24
        if hod in peak_hours:
            base = peak_weight
        elif hod < 6:
            base = 0.6
        else:
            base = 1.0
        doy = i // 24
        season = 1 + winter_boost * math.cos(2 * math.pi * (doy - 15) / 365)
        w.append(base * season)
    tot = sum(w)
    return [x * kwh / tot for x in w]


def load_from_csv(path):
    """Paskutinis skaitinis stulpelis, 8760 reikšmių (kWh per valandą)."""
    vals = []
    with open(path, newline="", encoding="utf-8") as f:
        for row in csv.reader(f):
            for cell in reversed(row):
                try:
                    vals.append(float(cell.replace(",", ".")))
                    break
                except ValueError:
                    continue
    if len(vals) < HOURS:
        raise ValueError(f"CSV turi {len(vals)} reikšmių, reikia bent {HOURS}")
    return vals[:HOURS]


class Model:
    def __init__(self, load, p):
        self.load = load
        self.p = p
        shape = build_gen_shape()
        self.gen_kwp = [s * p["yield"] for s in shape]  # kWh per kWp per valandą
        self.annual_load = sum(load)
        self.base_cost = self.annual_load * p["price"]
        self._cache = {}

    def simulate(self, pv_kwp, batt_kwh):
        key = (pv_kwp, batt_kwh)
        if key in self._cache:
            return self._cache[key]
        p = self.p
        eta = math.sqrt(p["batt_eff"])
        usable = batt_kwh * p["dod"]
        pmax = batt_kwh * p["batt_c"]
        soc = 0.0
        imp = exp = self_use = 0.0
        for l, gk in zip(self.load, self.gen_kwp):
            g = gk * pv_kwp
            direct = min(l, g)
            surplus = g - direct
            deficit = l - direct
            if batt_kwh > 0:
                ch = min(surplus, (usable - soc) / eta, pmax)
                if ch > 0:
                    soc += ch * eta
                    surplus -= ch
                dis = min(deficit, soc * eta, pmax)
                if dis > 0:
                    soc -= dis / eta
                    deficit -= dis
            imp += deficit
            exp += surplus
        gen = sum(self.gen_kwp) * pv_kwp
        res = {
            "import": imp,
            "export": exp,
            "generation": gen,
            "self_consumption_pct": 100 * (gen - exp) / gen if gen else 0.0,
            "self_sufficiency_pct": 100 * (1 - imp / self.annual_load),
        }
        self._cache[key] = res
        return res

    def annual_savings(self, pv_kwp, batt_kwh):
        r = self.simulate(pv_kwp, batt_kwh)
        cost = r["import"] * self.p["price"] - r["export"] * self.p["export_price"]
        return self.base_cost - cost

    def evaluate(self, pv_kwp, batt_kwh):
        p = self.p
        sav_pv = self.annual_savings(pv_kwp, 0)
        batt_extra = self.annual_savings(pv_kwp, batt_kwh) - sav_pv if batt_kwh > 0 else 0.0

        capex = pv_kwp * p["pv_cost"] + batt_kwh * p["batt_cost"]
        capex_net = capex * (1 - p["subsidy_pct"] / 100)

        cash = [-capex_net]
        for y in range(1, p["horizon"] + 1):
            pf = (1 + p["price_growth"]) ** (y - 1)
            s = sav_pv * (1 - p["pv_degradation"]) ** (y - 1) * pf
            if batt_kwh > 0 and y <= p["batt_life"]:
                s += batt_extra * pf * (1 - p["batt_fade"]) ** (y - 1)
            if y == p["inverter_year"] and pv_kwp > 0:
                s -= pv_kwp * p["inverter_cost"]
            cash.append(s)

        npv = sum(c / (1 + p["discount"]) ** t for t, c in enumerate(cash))
        cum, payback = 0.0, None
        for t, c in enumerate(cash):
            prev = cum
            cum += c
            if t > 0 and cum >= 0 and prev < 0:
                payback = (t - 1) + (-prev / c)
                break
        sim = self.simulate(pv_kwp, batt_kwh)
        return {
            "pv_kwp": pv_kwp,
            "batt_kwh": batt_kwh,
            "capex_eur": round(capex_net),
            "year1_savings_eur": round(self.annual_savings(pv_kwp, batt_kwh)),
            "battery_extra_year1_eur": round(batt_extra),
            "payback_years": None if payback is None else round(payback, 1),
            "npv_eur": round(npv),
            "generation_kwh": round(sim["generation"]),
            "self_consumption_pct": round(sim["self_consumption_pct"]),
            "self_sufficiency_pct": round(sim["self_sufficiency_pct"]),
        }


def analyze(housing, load, p):
    if housing == "butas":
        pv_opts = [0.4, 0.8]  # balkono / mini sistema
        batt_opts = [0, 2]
    else:
        pv_opts = [float(x) for x in range(2, 13)]
        batt_opts = [0, 5, 10, 15]

    m = Model(load, p)
    results = []
    for pv in pv_opts:
        for b in batt_opts:
            results.append(m.evaluate(pv, b))

    best_pv_only = max((r for r in results if r["batt_kwh"] == 0), key=lambda r: r["npv_eur"])
    best_any = max(results, key=lambda r: r["npv_eur"])
    best_with_batt = max(
        (r for r in results if r["batt_kwh"] > 0), key=lambda r: r["npv_eur"]
    )

    solar_ok = best_pv_only["npv_eur"] > 0
    batt_ok = best_any["batt_kwh"] > 0 and best_any["npv_eur"] > best_pv_only["npv_eur"] + 100

    if not solar_ok:
        verdict = "Saulės elektrinė pagal šias prielaidas neatsiperka per horizontą."
    elif batt_ok:
        verdict = "Verta saulės elektrinė IR baterija."
    else:
        verdict = "Verta saulės elektrinė, bet baterija neatsiperka."

    return {
        "verdict": verdict,
        "solar_worth_it": solar_ok,
        "battery_worth_it": batt_ok,
        "best_overall": best_any,
        "best_pv_only": best_pv_only,
        "best_with_battery": best_with_batt,
        "all_options": sorted(results, key=lambda r: -r["npv_eur"]),
        "annual_load_kwh": round(m.annual_load),
        "assumptions": p,
    }


DEFAULTS = {
    "price": 0.20,            # EUR/kWh visa pirkimo kaina su visais mokesčiais ir PVM
    "export_price": 0.06,     # EUR/kWh už į tinklą atiduotą perteklių
    "pv_cost": 1100.0,        # EUR/kWp įrengta
    "batt_cost": 550.0,       # EUR/kWh įrengta
    "subsidy_pct": 0.0,
    "discount": 0.04,
    "price_growth": 0.02,
    "horizon": 25,
    "yield": 950.0,           # kWh/kWp/metus (Vilnius, pietų stogas)
    "pv_degradation": 0.005,
    "batt_eff": 0.90,
    "dod": 0.90,
    "batt_c": 0.5,
    "batt_life": 15,
    "batt_fade": 0.02,
    "inverter_year": 12,
    "inverter_cost": 150.0,   # EUR/kWp
}


def fmt(r):
    pb = "-" if r["payback_years"] is None else f'{r["payback_years"]} m.'
    return (
        f'{r["pv_kwp"]:>4} kWp + {r["batt_kwh"]:>2} kWh bat. | '
        f'investicija {r["capex_eur"]:>6} EUR | 1-ų m. sutaupymas {r["year1_savings_eur"]:>5} EUR | '
        f'atsipirkimas {pb:>7} | NPV {r["npv_eur"]:>6} EUR | '
        f'savo suvartojimas {r["self_consumption_pct"]}%, savarankiškumas {r["self_sufficiency_pct"]}%'
    )


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--type", choices=["butas", "namas"], required=True)
    ap.add_argument("--kwh", type=float, help="metinis suvartojimas kWh")
    ap.add_argument("--peak-hours", default="18-22", help="pvz. 7-9,17-22")
    ap.add_argument("--load-csv", help="valandiniai duomenys (8760 eil.), pakeičia --kwh ir --peak-hours")
    ap.add_argument("--json", action="store_true")
    for k, v in DEFAULTS.items():
        ap.add_argument("--" + k.replace("_", "-"), type=type(v), default=v)
    a = ap.parse_args()

    p = {k: getattr(a, k) for k in DEFAULTS}
    if a.load_csv:
        load = load_from_csv(a.load_csv)
    else:
        if not a.kwh:
            ap.error("reikia --kwh arba --load-csv")
        load = build_load(a.kwh, parse_hours(a.peak_hours))

    res = analyze(a.type, load, p)

    if a.json:
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return

    print(f'\nBūstas: {a.type}, metinis suvartojimas: {res["annual_load_kwh"]} kWh')
    print(f'\n>>> {res["verdict"]}\n')
    print("Geriausias variantas:      ", fmt(res["best_overall"]))
    print("Geriausias be baterijos:   ", fmt(res["best_pv_only"]))
    print("Geriausias su baterija:    ", fmt(res["best_with_battery"]))
    print("\nTop 5 pagal NPV:")
    for r in res["all_options"][:5]:
        print("  ", fmt(r))
    print(
        "\nPrielaidos (patikrinkite!): pirkimo kaina %.2f EUR/kWh, pertekliaus supirkimas %.2f EUR/kWh, "
        "PV %.0f EUR/kWp, baterija %.0f EUR/kWh, kainų augimas %.0f%%/m., diskontas %.0f%%."
        % (p["price"], p["export_price"], p["pv_cost"], p["batt_cost"], 100 * p["price_growth"], 100 * p["discount"])
    )


if __name__ == "__main__":
    main()
