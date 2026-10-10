"""
Downloads Nord Pool day-ahead prices for the Lithuanian price area from the public Elering API
(https://dashboard.elering.ee/api/nps/price, no key needed) and writes hourly averages in local
time (Europe/Vilnius) to Data/Prices/np-lt.csv.

    python -X utf8 tools/prices/fetch_np.py 2025-10-01 2026-10-01

Output columns: date (YYYY-MM-DD), hour (0-23, local clock), price (EUR/kWh, without VAT).
Since 2025-10-01 the market uses 15-minute intervals, so every hour is the mean of its intervals.
The missing hour on the spring DST day is filled with the previous hour's price.
"""
import json
import sys
import urllib.request
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path

API = "https://dashboard.elering.ee/api/nps/price?start={start}&end={end}"


def last_sunday(year, month):
    d = datetime(year, month + 1, 1) - timedelta(days=1) if month < 12 else datetime(year, 12, 31)
    while d.weekday() != 6:
        d -= timedelta(days=1)
    return d


def to_local(utc):
    """EU rule: summer time from the last Sunday of March 01:00 UTC to the last Sunday of October 01:00 UTC."""
    start = last_sunday(utc.year, 3).replace(hour=1, tzinfo=timezone.utc)
    end = last_sunday(utc.year, 10).replace(hour=1, tzinfo=timezone.utc)
    offset = 3 if start <= utc < end else 2
    return (utc + timedelta(hours=offset)).replace(tzinfo=None)


def fetch(start, end):
    url = API.format(start=start.strftime("%Y-%m-%dT%H:%M:%S.000Z"), end=end.strftime("%Y-%m-%dT%H:%M:%S.000Z"))
    with urllib.request.urlopen(url, timeout=60) as r:
        body = json.load(r)
    if not body.get("success"):
        raise RuntimeError(f"API error for {url}")
    return body["data"]["lt"]


def main():
    first = datetime.fromisoformat(sys.argv[1] if len(sys.argv) > 1 else "2025-10-01")
    last = datetime.fromisoformat(sys.argv[2] if len(sys.argv) > 2 else "2026-10-01")

    buckets = defaultdict(list)
    cursor = first - timedelta(hours=3)  # local midnight in UTC (worst case)
    stop = last
    while cursor < stop:
        chunk_end = min(cursor + timedelta(days=31), stop)
        for row in fetch(cursor.replace(tzinfo=timezone.utc), chunk_end.replace(tzinfo=timezone.utc)):
            local = to_local(datetime.fromtimestamp(row["timestamp"], tz=timezone.utc))
            if first <= local < last:
                buckets[(local.date(), local.hour)].append(row["price"] / 1000)
        cursor = chunk_end

    out = Path(__file__).resolve().parents[2] / "Data" / "Prices" / "np-lt.csv"
    out.parent.mkdir(parents=True, exist_ok=True)
    lines = ["date,hour,price"]
    day = first.date()
    previous = None
    while day < last.date():
        for hour in range(24):
            values = buckets.get((day, hour))
            price = sum(values) / len(values) if values else previous
            if price is None:
                raise RuntimeError(f"No price for {day} {hour}:00")
            lines.append(f"{day.isoformat()},{hour},{price:.5f}")
            previous = price
        day += timedelta(days=1)

    out.write_text("\n".join(lines) + "\n", encoding="utf-8")
    prices = [float(l.split(",")[2]) for l in lines[1:]]
    print(f"{out}: {len(prices)} h, mean {sum(prices) / len(prices):.4f} EUR/kWh, min {min(prices):.4f}, max {max(prices):.4f}")


if __name__ == "__main__":
    main()
