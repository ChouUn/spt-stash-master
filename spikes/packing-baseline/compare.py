"""Pair two frozen Windows replay builds on every corpus case, serially.

python spikes/packing-baseline/compare.py --before BEFORE.exe --after AFTER.exe \
    --corpus corpus.json --output new-results-directory --seconds 3

Runs on Windows or WSL. Output must not exist. Every case gets a fresh packer;
AB/BA order alternates within each family. No solver calls run concurrently.
Input generation is deterministic; time-limited solver outputs are not.
"""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import statistics
import subprocess
import time


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def runtime_path(path):
    path = Path(path).resolve()
    if os.name == "nt":
        return str(path)
    return subprocess.check_output(["wslpath", "-w", str(path)], text=True).strip()


def percentile(values, fraction):
    values = sorted(values)
    position = (len(values) - 1) * fraction
    lower = math.floor(position)
    upper = math.ceil(position)
    return values[lower] + (values[upper] - values[lower]) * (position - lower)


def timing(values):
    return {"mean": statistics.mean(values), "median": statistics.median(values),
            "p95": percentile(values, .95), "min": min(values), "max": max(values)}


def summarize(pairs):
    output = {}
    for family in sorted({p["Family"] for p in pairs}):
        rows = [p for p in pairs if p["Family"] == family]
        valid = [p for p in rows if p["Before"]["Valid"] and p["After"]["Valid"]]
        diffs = [int(p["After"]["Score"]) - int(p["Before"]["Score"]) for p in valid]
        changes = []
        for p in valid:
            grid_deltas = []
            for before, after in zip(p["Before"]["Grids"], p["After"]["Grids"]):
                if before["Id"] != after["Id"]:
                    raise ValueError("Mismatched result grids")
                if int(before["Score"]) != int(after["Score"]):
                    grid_deltas.append({"Id": before["Id"], "BeforeHeight": before["Height"],
                                        "AfterHeight": after["Height"], "BeforeSpans": before["Spans"],
                                        "AfterSpans": after["Spans"], "BeforeScore": before["Score"],
                                        "AfterScore": after["Score"]})
            if grid_deltas:
                changes.append({"CaseId": p["CaseId"], "Grids": grid_deltas})
        output[family] = {
            "cases": len(rows), "validPairs": len(valid),
            "afterWins": sum(d < 0 for d in diffs), "ties": sum(d == 0 for d in diffs),
            "afterLosses": sum(d > 0 for d in diffs),
            "beforeInvalid": sum(not p["Before"]["Valid"] for p in rows),
            "afterInvalid": sum(not p["After"]["Valid"] for p in rows),
            "beforeWarnings": sum(bool(p["Before"]["Warning"]) for p in rows),
            "afterWarnings": sum(bool(p["After"]["Warning"]) for p in rows),
            "beforeMs": timing([p["Before"]["ElapsedMs"] for p in rows]),
            "afterMs": timing([p["After"]["ElapsedMs"] for p in rows]),
            "pairedAfterMinusBeforeMs": timing([p["After"]["ElapsedMs"] - p["Before"]["ElapsedMs"] for p in rows]),
            "qualityChanges": changes,
            "beforeSlotStageCases": sum("slots[" in (p["Before"]["Diagnostic"] or "") for p in rows),
            "afterSlotStageCases": sum("slots[" in (p["After"]["Diagnostic"] or "") for p in rows),
        }
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before", required=True, type=Path)
    parser.add_argument("--after", required=True, type=Path)
    parser.add_argument("--corpus", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--seconds", type=float, default=3)
    args = parser.parse_args()
    if not math.isfinite(args.seconds) or args.seconds <= 0:
        parser.error("seconds must be positive and finite")
    corpus = json.loads(args.corpus.read_text(encoding="utf-8-sig"))
    if corpus["Schema"] != 3 or not corpus["Cases"]:
        parser.error("requires nonempty schema 3 corpus")
    args.output.mkdir(parents=True, exist_ok=False)
    metadata = {
        "corpusSha256": digest(args.corpus), "seed": corpus["Seed"],
        "generatorVersion": corpus["GeneratorVersion"], "cases": len(corpus["Cases"]),
        "secondsPerCaseSharedAcrossGrids": args.seconds, "platform": platform.platform(),
        "logicalCpus": os.cpu_count(), "startedUnix": time.time(),
        "protocol": "Warm each process once; one fresh-cache call per independent case per version. Serial AB/BA alternating within family. No output feedback. Time PackAll only.",
        "comparisonScriptSha256": digest(__file__),
        "versions": {},
    }
    processes = {}
    logs = []
    pairs = []
    try:
        for name, exe in [("Before", args.before), ("After", args.after)]:
            exe = exe.resolve()
            core = exe.parent / "ChouUn.StashMaster.Core.dll"
            metadata["versions"][name] = {"executable": str(exe), "exeSha256": digest(exe),
                                           "coreSha256": digest(core)}
            log = (args.output / (name.lower() + "-stderr.log")).open("x", encoding="utf-8")
            logs.append(log)
            p = subprocess.Popen([str(exe), "replay", "--input", runtime_path(args.corpus),
                                  "--seconds", str(args.seconds)], stdin=subprocess.PIPE,
                                 stdout=subprocess.PIPE, stderr=log, text=True, encoding="utf-8")
            processes[name] = p
            if p.stdout.readline().strip() != "READY":
                raise RuntimeError(name + " failed warmup; see stderr log")
        (args.output / "protocol.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
        counts = {}
        with (args.output / "pairs.jsonl").open("x", encoding="utf-8") as stream:
            for index, case in enumerate(corpus["Cases"]):
                count = counts.get(case["Family"], 0)
                counts[case["Family"]] = count + 1
                order = ["Before", "After"] if count % 2 == 0 else ["After", "Before"]
                pair = {"CaseId": case["Id"], "Family": case["Family"], "Order": order}
                for name in order:
                    p = processes[name]
                    p.stdin.write(str(index) + "\n")
                    p.stdin.flush()
                    line = p.stdout.readline()
                    if not line:
                        raise RuntimeError(name + " exited while solving " + case["Id"])
                    result = json.loads(line)
                    if result["CaseId"] != case["Id"] or result["Family"] != case["Family"]:
                        raise ValueError("Mismatched replay case")
                    pair[name] = result
                pairs.append(pair)
                stream.write(json.dumps(pair, separators=(",", ":")) + "\n")
                stream.flush()
                print(f"{index+1}/{len(corpus['Cases'])} {case['Id']} "
                      f"before={pair['Before']['ElapsedMs']:.1f}ms after={pair['After']['ElapsedMs']:.1f}ms", flush=True)
        for name, p in processes.items():
            p.stdin.close()
            if p.wait(timeout=30) != 0:
                raise RuntimeError(name + " exited unsuccessfully")
        summary = {"protocol": metadata, "families": summarize(pairs), "completedUnix": time.time(),
                   "limits": ["One measurement per independent case, not an estimate of per-case solver variance.",
                              "Synthetic feasible final-packing inputs; no game container filters or Organizer prefix transactions.",
                              "Fixed corpus generation does not make time-limited CP-SAT deterministic.",
                              "Compare scores only within the same case, not averaged across differently weighted grids."]}
        (args.output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({f: {k: v for k, v in s.items() if k != "qualityChanges"}
                          for f, s in summary["families"].items()}, indent=2))
    finally:
        for p in processes.values():
            if p.poll() is None:
                p.terminate()
                try:
                    p.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    p.kill()
                    p.wait()
        for log in logs:
            log.close()


if __name__ == "__main__":
    main()
