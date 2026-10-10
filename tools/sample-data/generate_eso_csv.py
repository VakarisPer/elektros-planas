"""
Generates a synthetic ESO hourly export ("Valandiniai[...].csv") for a made-up household: an ordinary
consumer, optionally with an electric car and/or a solar plant (then it also gives energy to the grid).

    python -X utf8 generate_eso_csv.py
    python -X utf8 generate_eso_csv.py --profile house-heated --start 2025-01-01 --end 2025-12-31
    python -X utf8 generate_eso_csv.py --annual-kwh 3200 --seed 7 --out my.csv
    python -X utf8 generate_eso_csv.py --profile house --solar-kwp 10 --ev --start 2025-01-01 --end 2026-10-09

The layout copies a real ESO export: UTF-8 BOM, LF line endings, ';' separator, every field quoted, one row
per hour per energy type (all P+ rows, then P-, Q+, Q-), ISO timestamps with the local UTC offset (the
spring DST day has no 03:00, the autumn day prints 02:00+03:00 then 03:00+02:00). The person is made up.

Energy types: P+ = taken from the grid, P- = given to the grid (only with --solar-kwp),
Q+/Q- = reactive energy (approximate; the app ignores it).

Profiles (typical yearly consumption, can be overridden with --annual-kwh):
    apartment      ~2400 kWh  small flat, gas/district heating
    house          ~4200 kWh  house, non-electric heating
    house-heated   ~9500 kWh  house heated with electricity (heat pump / convectors), clear winter peak

--annual-kwh is the household's own consumption. With a solar plant part of it is covered directly by
the plant, so P+ is smaller and P- holds the surplus (~950 kWh per kWp per year in Vilnius).

Only the Python standard library is used. The result is reproducible for a given --seed.
"""
import argparse
import math
import random
from datetime import date, timedelta
from pathlib import Path

HEADER = [
    "Vartotojo kodas", "Objekto nr.", "Asmens / įmonės kodas", "Asmens vardas pavardė / įmonės pavadinimas",
    "Objekto pavadinimas", "Objekto tipas", "Objekto adresas", "Leistinoji galia, kW", "Viso skaitiklių",
    "Skaitiklio nr.", "Data, valanda", "Energijos tipas", "Suvartojimas", "Kiekis, kWh",
]

# base_kw: always-on load (fridge, router, standby); activity_kw: extra load at peak of the daily shape;
# heat_kw_per_deg: electric heating power per degree below 15 °C; permitted_kw: "Leistinoji galia".
PROFILES = {
    "apartment": dict(annual=2400, base_kw=0.07, activity_kw=0.55, heat_kw_per_deg=0.0, permitted_kw=10,
                      object_name="BUTAS", address="Pavyzdžio g. 1-1, 01234 Vilnius"),
    "house": dict(annual=4200, base_kw=0.12, activity_kw=0.80, heat_kw_per_deg=0.0, permitted_kw=11,
                  object_name="GYVENAMASIS NAMAS", address="Pavyzdžio g. 1, 01234 Vilnius"),
    "house-heated": dict(annual=9500, base_kw=0.14, activity_kw=0.85, heat_kw_per_deg=0.10, permitted_kw=15,
                         object_name="GYVENAMASIS NAMAS", address="Pavyzdžio g. 1, 01234 Vilnius"),
}

# Hourly shape of household activity (0-23). Weekdays: morning and evening peaks; weekends: later and flatter.
WEEKDAY = [0.10, 0.07, 0.06, 0.06, 0.08, 0.20, 0.55, 0.75, 0.45, 0.25, 0.20, 0.20,
           0.25, 0.22, 0.20, 0.22, 0.35, 0.65, 0.95, 1.00, 0.90, 0.70, 0.40, 0.20]
WEEKEND = [0.12, 0.08, 0.06, 0.06, 0.06, 0.08, 0.15, 0.30, 0.55, 0.75, 0.80, 0.75,
           0.70, 0.55, 0.45, 0.45, 0.55, 0.75, 0.95, 1.00, 0.90, 0.70, 0.40, 0.20]

# Share of clear-sky sun that reaches the panels on an average day, January..December (Vilnius).
MONTH_SUN = [0.30, 0.45, 0.60, 0.70, 0.78, 0.80, 0.80, 0.76, 0.65, 0.48, 0.30, 0.25]

LATITUDE = math.radians(54.7)
SOLAR_YIELD = 950  # kWh per kWp per year
EV_KWH_PER_YEAR = 2200  # ~1 000 km per month, charged at home at night


def last_sunday(year, month):
    d = date(year, month + 1, 1) - timedelta(days=1) if month < 12 else date(year, 12, 31)
    return d - timedelta(days=(d.weekday() + 1) % 7)


def hours_of_day(day):
    """(hour, utc offset) pairs ESO prints for one local day: the spring DST day has no 03:00 and
    the autumn day has 24 rows, 00-02 at +03:00 and 03-23 at +02:00 (same as the real export)."""
    spring, autumn = last_sunday(day.year, 3), last_sunday(day.year, 10)
    for h in range(24):
        if day == spring and h == 3:
            continue
        summer = (spring < day < autumn) or (day == spring and h >= 4) or (day == autumn and h < 3)
        yield h, 3 if summer else 2


def daily_temperature(day, rng, state):
    """Synthetic Vilnius mean daily temperature: seasonal cosine (coldest mid-January) + a slow random walk."""
    doy = day.timetuple().tm_yday
    seasonal = 7.0 - 13.5 * math.cos(2 * math.pi * (doy - 20) / 365)
    state[0] = 0.8 * state[0] + rng.gauss(0, 2.2)
    return seasonal + state[0]


def vacation_days(start, end, rng):
    """One summer trip and sometimes a winter one: the house is empty, only the base load remains."""
    away = set()
    for year in range(start.year, end.year + 1):
        trips = [(date(year, 6, 15), date(year, 8, 20), rng.randint(7, 14))]
        if rng.random() < 0.5:
            trips.append((date(year, 12, 27), date(year, 12, 29), rng.randint(3, 6)))
        for lo, hi, length in trips:
            first = lo + timedelta(days=rng.randint(0, max((hi - lo).days, 0)))
            away.update(first + timedelta(days=i) for i in range(length))
    return away


def sun_height(day, hour, offset):
    """sin(solar elevation) in the middle of a local clock hour (0 when the sun is down)."""
    doy = day.timetuple().tm_yday
    declination = math.radians(23.45) * math.sin(2 * math.pi * (284 + doy) / 365)
    solar_time = hour + 0.5 - (offset - 25.3 / 15)  # UTC+offset clock vs solar time at 25.3° E
    angle = math.radians(15 * (solar_time - 12))
    s = math.sin(LATITUDE) * math.sin(declination) + math.cos(LATITUDE) * math.cos(declination) * math.cos(angle)
    return max(0.0, s)


def build_profile(profile, start, end, rng, ev):
    """Raw rows for every local hour in [start, end]: (day, hour, offset, load_kw, sun, ev_kwh).
    load_kw is unscaled household consumption, sun the unscaled solar shape (clouds included)."""
    p = PROFILES[profile]
    away = vacation_days(start, end, rng)
    temp_state = [0.0]
    rows = []
    day = start
    while day <= end:
        temp = daily_temperature(day, rng, temp_state)
        shape = WEEKEND if day.weekday() >= 5 else WEEKDAY
        winter = 1.0 + 0.22 * math.cos(2 * math.pi * (day.timetuple().tm_yday - 15) / 365)  # lighting, more time indoors
        mood = rng.lognormvariate(0, 0.22)  # some days are simply busier
        is_away = day in away
        # Cloudiness of the day: mostly around the monthly average, sometimes fully overcast or clear.
        clouds = min(1.0, max(0.05, rng.gauss(MONTH_SUN[day.month - 1], 0.22)))
        # The car is charged at home on ~5 of 7 nights, starting around 22-01 h for a few hours.
        ev_start, ev_hours = (rng.choice([22, 23, 0, 1]), rng.randint(3, 5)) if ev and not is_away and rng.random() < 5 / 7 else (None, 0)
        for hour, offset in hours_of_day(day):
            kw = p["base_kw"] * rng.uniform(0.85, 1.2)
            if not is_away:
                kw += p["activity_kw"] * shape[hour] * winter * mood * rng.lognormvariate(0, 0.3)
                if rng.random() < 0.05 * shape[hour]:  # oven, washing machine, dishwasher, kettle...
                    kw += rng.uniform(0.8, 2.5)
            if p["heat_kw_per_deg"] and temp < 15:
                night_setback = 0.75 if 0 <= hour < 6 else 1.0
                kw += p["heat_kw_per_deg"] * (15 - temp) * night_setback * rng.uniform(0.85, 1.15) * (0.6 if is_away else 1.0)
            charging = ev_start is not None and (hour - ev_start) % 24 < ev_hours
            sun = sun_height(day, hour, offset) * clouds * rng.uniform(0.85, 1.15)
            rows.append((day, hour, offset, kw, sun, 1.0 if charging else 0.0))
        day += timedelta(days=1)
    return rows


def write_csv(path, flows, profile, args, rng):
    """flows: (day, hour, offset, import_kwh, export_kwh). Rows are grouped by energy type like ESO does."""
    p = PROFILES[profile]
    object_type = "Saulės elektrinė savoms reikmėms" if args.solar_kwp else "Vartojimo objektas"
    identity = [args.user_code, args.object_no, args.person_code, args.name, p["object_name"],
                object_type, p["address"], str(p["permitted_kw"]), "1", ""]
    lines = [";".join(f'"{h}"' for h in HEADER)]
    for kind in ("P+", "P-", "Q+", "Q-"):
        for day, hour, offset, imp, exp in flows:
            if kind == "P+":
                value = imp
            elif kind == "P-":
                value = exp
            elif kind == "Q+":  # inductive reactive energy, roughly 10-25 % of active
                value = imp * rng.uniform(0.10, 0.25) if imp > 0.05 else 0.0
            else:  # capacitive, small (inverters add some)
                value = (imp + exp) * rng.uniform(0.0, 0.05)
            stamp = f"{day.isoformat()}T{hour:02d}:00:00+{offset:02d}:00"
            fields = identity + [stamp, kind, "Faktinis", f"{value:.3f}"]
            lines.append(";".join(f'"{f}"' for f in fields))
    with open(path, "w", encoding="utf-8-sig", newline="") as f:
        f.write("\n".join(lines) + "\n")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--profile", choices=PROFILES, default="house")
    ap.add_argument("--start", type=date.fromisoformat, default=date(2025, 1, 1))
    ap.add_argument("--end", type=date.fromisoformat, default=date(2025, 12, 31))
    ap.add_argument("--annual-kwh", type=float, help="own consumption per 365 days without the EV (default: profile typical)")
    ap.add_argument("--solar-kwp", type=float, default=0, help="solar plant size, kWp (0 = no plant, no export)")
    ap.add_argument("--ev", action="store_true", help=f"an electric car charged at home at night (~{EV_KWH_PER_YEAR} kWh/year)")
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--name", default="JONAS JONAITIS")
    ap.add_argument("--user-code", default="10000001")
    ap.add_argument("--object-no", default="10000002")
    ap.add_argument("--person-code", default="30000000000")
    ap.add_argument("--out", type=Path, help="default: Valandiniai[<start> - <end>].csv in the current folder")
    args = ap.parse_args()
    if args.end < args.start:
        ap.error("--end must not be before --start")
    if args.solar_kwp < 0:
        ap.error("--solar-kwp must not be negative")

    rng = random.Random(args.seed)
    # Build and scale whole calendar years so a partial period (e.g. only January) still gets a realistic
    # level for that season, then cut out the requested range.
    year_start, year_end = date(args.start.year, 1, 1), date(args.end.year, 12, 31)
    full = build_profile(args.profile, year_start, year_end, rng, args.ev)
    years = ((year_end - year_start).days + 1) / 365.25
    annual = args.annual_kwh or PROFILES[args.profile]["annual"]
    load_scale = annual * years / sum(r[3] for r in full)
    sun_total = sum(r[4] for r in full)
    sun_scale = SOLAR_YIELD * args.solar_kwp * years / sun_total if sun_total > 0 else 0
    ev_total = sum(r[5] for r in full)
    ev_scale = EV_KWH_PER_YEAR * years / ev_total if ev_total > 0 else 0

    flows = []
    totals = dict(load=0.0, gen=0.0, imp=0.0, exp=0.0)
    for day, hour, offset, kw, sun, ev in full:
        if not args.start <= day <= args.end:
            continue
        load = kw * load_scale + ev * ev_scale
        gen = sun * sun_scale
        imp, exp = max(0.0, load - gen), max(0.0, gen - load)
        flows.append((day, hour, offset, imp, exp))
        totals["load"] += load
        totals["gen"] += gen
        totals["imp"] += imp
        totals["exp"] += exp

    out = args.out or Path(f"Valandiniai[{args.start:%Y-%m} - {args.end:%Y-%m}].csv")
    write_csv(out, flows, args.profile, args, rng)

    days = (args.end - args.start).days + 1
    print(f"{out}: {len(flows)} hours x 4 energy types over {days} days; consumption {totals['load']:.0f} kWh, "
          f"generation {totals['gen']:.0f} kWh, P+ (import) {totals['imp']:.0f} kWh, P- (export) {totals['exp']:.0f} kWh")


if __name__ == "__main__":
    main()
