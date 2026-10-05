"""Run each watch-only event in its own stream using unchanged pinned research functions.

The original six trading streams prohibit overlap. The observer intentionally retains every
trigger; independent one-event streams preserve all execution/OI/H5 rules without adding a
position gate to the observer. Never touch the sealed set or change research source/data.
"""
import csv
import runpy
import json

reference = runpy.run_path("tools/adaptive_weak2_option_trade_simulation.py")
with open("adaptive-threshold-reference.json", "w") as stream:
    json.dump(reference["strong_threshold"], stream, sort_keys=True)
simulate = reference["simulate_stream"]
original = simulate.__globals__["candidates"]
observations = []
try:
    for candidate in original:
        simulate.__globals__["candidates"] = [candidate]
        rows, _ = simulate("INDEPENDENT_WATCH_OBSERVATION", lambda _: True, "none")
        observations.extend(rows)
finally:
    simulate.__globals__["candidates"] = original
if not observations:
    raise AssertionError("No independent reference observations")
with open("adaptive-observation-reference.csv", "w", newline="") as stream:
    writer = csv.DictWriter(stream, fieldnames=list(observations[0]))
    writer.writeheader()
    writer.writerows(observations)
print(f"REFERENCE: {len(observations)} independent overlapping watch-only observations")
