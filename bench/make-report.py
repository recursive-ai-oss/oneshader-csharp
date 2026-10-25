#!/usr/bin/env python3
"""Turns the benchmark results into docs/performance.md's tables and the hero charts.

    bench/make-report.py [RESULTS_DIR]        (default: bench/results)

Reads  RESULTS_DIR/process.json            whole-process timing against DXC (OneShader.Bench process)
       RESULTS_DIR/inproc/*-report-full.json   BenchmarkDotNet results (OneShader.Bench inproc)
       RESULTS_DIR/inproc/unsupported-*.tsv    files a target refuses
       RESULTS_DIR/inproc/load.json            load average while BenchmarkDotNet ran
Writes the blocks between <!-- begin:NAME --> and <!-- end:NAME --> in docs/performance.md,
       docs/img/oneshader-hero-light.svg and docs/img/oneshader-hero-dark.svg.
Standard library only.
"""
import json
import math
import os
import re
import statistics
import sys
from html import escape

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
DOC = os.path.join(ROOT, "docs", "performance.md")
IMG = os.path.join(ROOT, "docs", "img")
TIE = 0.05  # a time within 5% of DXC's is a tie

TOOLS = [("aot", "NativeAOT"), ("r2r", "ReadyToRun"), ("jit", "JIT")]


def med(xs):
    return statistics.median(xs)


def geomean(xs):
    return math.exp(sum(math.log(x) for x in xs) / len(xs))


def ms(v):
    if v < 10:
        return f"{v:.2f}"
    if v < 100:
        return f"{v:.1f}"
    return f"{v:.0f}"


def us_or_ms(ns):
    if ns < 1e6:
        return f"{ns / 1e3:.0f} µs"
    return f"{ns / 1e6:.2f} ms"


def kib(b):
    return f"{b / 1024:.0f}" if b >= 10240 else f"{b / 1024:.1f}"


def verdict(ratio):
    """ratio = DXC time / our time. >1 means we are faster."""
    if abs(ratio - 1) <= TIE:
        return "tie"
    if ratio > 1:
        return f"{ratio:.2f}× faster" if ratio < 10 else f"{ratio:.1f}× faster"
    return f"{1 / ratio:.2f}× slower" if 1 / ratio < 10 else f"{1 / ratio:.1f}× slower"


def group_of(unit):
    top = unit["file"].split("/")[0]
    return {"2d": "2D renderer", "3d": "3D (PBR set)", "compute": "compute", "lang": "language features"}.get(top, top)


def check_label(label, name):
    """BenchmarkDotNet shortens long parameter values to 'head(...)tail [length]'; rows are matched to names by order, and this checks the match."""
    m = re.match(r"^(.*)\(\.\.\.\)(.*) \[(\d+)\]$", label)
    ok = label == name if not m else (name.startswith(m.group(1)) and name.endswith(m.group(2)) and len(name) == int(m.group(3)))
    if not ok:
        raise SystemExit(f"benchmark row '{label}' does not match parameter '{name}'")


def load_process(results):
    with open(os.path.join(results, "process.json")) as f:
        return json.load(f)


def stats(samples):
    return med(samples), min(samples), max(samples)


def cell(samples):
    m, lo, hi = stats(samples)
    return f"{ms(m)} ({ms(lo)}–{ms(hi)})"


def process_tables(p):
    units = p["units"]
    tools = [(k, n) for k, n in TOOLS if f"{k}_warm_ms" in units[0]]
    out = {}

    def table(kind):
        # kind: "warm" (median and range of the repeats) or "cold" (the single first run)
        head = "| Entry point | DXC ms | " + " | ".join(f"{n} ms | vs DXC" for _, n in tools) + " |"
        sep = "|---|--:|" + "|".join("--:|---" for _ in tools) + "|"
        rows = [head, sep]
        for u in units:
            if kind == "warm":
                d = med(u["dxc_warm_ms"])
                dcell = cell(u["dxc_warm_ms"])
                cells = []
                for k, _ in tools:
                    o = med(u[f"{k}_warm_ms"])
                    cells.append(f"{cell(u[f'{k}_warm_ms'])} | {verdict(d / o)}")
            else:
                d = u["dxc_cold_ms"]
                dcell = ms(d)
                cells = []
                for k, _ in tools:
                    o = u[f"{k}_cold_ms"]
                    cells.append(f"{ms(o)} | {verdict(d / o)}")
            rows.append(f"| `{u['id']}` | {dcell} | " + " | ".join(cells) + " |")
        return "\n".join(rows)

    out["process-warm"] = table("warm")
    out["process-cold"] = table("cold")

    # Size table
    rows = ["| Entry point | DXC words | OneShader words | OneShader vs DXC | Without debug names | vs DXC |", "|---|--:|--:|--:|--:|--:|"]
    tot = [0, 0, 0]
    for u in units:
        dw, ow, nw = u["dxc_bytes"] // 4, u["aot_bytes"] // 4, u["ours_nonames_bytes"] // 4
        tot[0] += dw
        tot[1] += ow
        tot[2] += nw
        rows.append(f"| `{u['id']}` | {dw} | {ow} | {pct(ow / dw)} | {nw} | {pct(nw / dw)} |")
    rows.append(f"| **All {len(units)} entry points** | **{tot[0]}** | **{tot[1]}** | **{pct(tot[1] / tot[0])}** | **{tot[2]}** | **{pct(tot[2] / tot[0])}** |")
    out["size"] = "\n".join(rows)
    return out


def pct(r):
    d = (r - 1) * 100
    return f"{d:+.1f}%"


def summary_rows(p):
    """(label, ratio list) for the summary table and the chart: ratios are DXC time / our time per entry point."""
    units = p["units"]
    groups = []
    for u in units:
        g = group_of(u)
        if g not in groups:
            groups.append(g)

    def ratios(kind, tool, sel=None):
        r = []
        for u in units:
            if sel and group_of(u) != sel:
                continue
            if kind == "warm":
                r.append(med(u["dxc_warm_ms"]) / med(u[f"{tool}_warm_ms"]))
            else:
                r.append(u["dxc_cold_ms"] / u[f"{tool}_cold_ms"])
        return r

    rows = []
    for g in groups:
        rows.append((f"NativeAOT, warm: {g}", ratios("warm", "aot", g)))
    rows.append(("NativeAOT, warm: all", ratios("warm", "aot")))
    rows.append(("NativeAOT, cold: all", ratios("cold", "aot")))
    if "r2r_warm_ms" in units[0]:
        rows.append(("ReadyToRun, warm: all", ratios("warm", "r2r")))
        rows.append(("ReadyToRun, cold: all", ratios("cold", "r2r")))
    rows.append(("JIT, warm: all", ratios("warm", "jit")))
    rows.append(("JIT, cold: all", ratios("cold", "jit")))
    return rows


def summary_table(rows):
    out = ["| Task (one process per entry point) | Entry points | Time vs DXC (geometric mean) | Fastest | Slowest | Entry points slower than DXC |", "|---|--:|---|---|---|--:|"]
    for label, r in rows:
        g = geomean(r)
        slower = sum(1 for x in r if x < 1 - TIE)
        out.append(f"| {label} | {len(r)} | {verdict(g)} | {verdict(max(r))} | {verdict(min(r))} | {slower} |")
    return "\n".join(out)


def inproc_table(results):
    d = os.path.join(results, "inproc")
    data = {}
    for cls, key in [("SpirvOptimized", "spirv"), ("SpirvUnoptimized", "spirv0"), ("MslOptimized", "msl"), ("WgslOptimized", "wgsl")]:
        path = os.path.join(d, f"OneShader.Bench.{cls}-report-full.json")
        data[key] = {}
        if not os.path.exists(path):
            continue
        with open(path) as f:
            j = json.load(f)
        plist = os.path.join(d, "params-" + {"spirv": "SpirV", "spirv0": "SpirV", "msl": "Msl", "wgsl": "Wgsl"}[key] + ".tsv")
        with open(plist) as pf:
            order = [l.rstrip("\n") for l in pf if l.strip()]
        if len(order) != len(j["Benchmarks"]):
            raise SystemExit(f"{path}: {len(j['Benchmarks'])} rows but {len(order)} parameters")
        for b, name in zip(j["Benchmarks"], order):
            check_label(re.match(r"Shader=(.*)", b["Parameters"]).group(1), name)
            data[key][name] = (b["Statistics"]["Mean"], b["Statistics"]["StandardDeviation"], b["Memory"]["BytesAllocatedPerOperation"])
        data[key + "_host"] = j["HostEnvironmentInfo"]
    names = list(data["spirv"].keys())
    refused = {}
    for t in ("Msl", "Wgsl"):
        p = os.path.join(d, f"unsupported-{t}.tsv")
        refused[t.lower()] = {}
        if os.path.exists(p):
            for line in open(p):
                if line.strip():
                    n, msg = line.rstrip("\n").split("\t", 1)
                    refused[t.lower()][n] = msg
    head = "| File | SPIR-V ms | alloc KiB | SPIR-V, optimizer off ms | alloc KiB | MSL ms | alloc KiB | WGSL ms | alloc KiB |"
    rows = [head, "|---|--:|--:|--:|--:|--:|--:|--:|--:|"]
    tot = {k: 0.0 for k in ("spirv", "spirv0", "msl", "wgsl")}
    for n in names:
        cells = []
        for k in ("spirv", "spirv0", "msl", "wgsl"):
            if n in data[k]:
                mean, sd, alloc = data[k][n]
                tot[k] += mean
                cells.append(f"{mean / 1e6:.3f} (±{sd / 1e6:.3f}) | {kib(alloc)}")
            else:
                cells.append("refused | –")
        rows.append(f"| `{n}` | " + " | ".join(cells) + " |")
    return "\n".join(rows), data, refused, names, tot


def load_line(p):
    during = p.get("loadDuring", [])
    return (p["loadStart"], p["loadEnd"], (min(during), max(during), len(during)) if during else None)


# ---- charts -----------------------------------------------------------------------------------

PALETTES = {
    "light": dict(bg="#ffffff", fg="#1f2328", axis="#8c959f", grid="#d8dee4", win="#2da44e", loss="#cf222e", tie="#8c959f"),
    "dark": dict(bg="#0d1117", fg="#e6edf3", axis="#7d8590", grid="#30363d", win="#3fb950", loss="#f85149", tie="#7d8590"),
}


def chart(rows, mode):
    pal = PALETTES[mode]
    left, plot_w, top, row_h, right = 330, 520, 70, 34, 150
    lo = min(math.log2(geomean(r)) for _, r in rows)
    hi = max(math.log2(geomean(r)) for _, r in rows)
    lim = max(2, math.ceil(max(abs(lo), abs(hi))))
    width = left + plot_w + right
    height = top + row_h * len(rows) + 50
    cx = left + plot_w / 2
    scale = (plot_w / 2) / lim
    s = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" role="img" font-family="system-ui, -apple-system, Segoe UI, Helvetica, Arial, sans-serif">']
    s.append(f'<title>How many times faster OneShader is than DXC, per task</title>')
    s.append(f'<rect width="{width}" height="{height}" fill="{pal["bg"]}"/>')
    s.append(f'<text x="16" y="28" font-size="17" font-weight="600" fill="{pal["fg"]}">OneShader compile time vs DXC (SPIR-V)</text>')
    s.append(f'<text x="16" y="48" font-size="12" fill="{pal["axis"]}">Whole process per entry point, geometric mean over the corpus, log scale. Right of the line: OneShader is faster.</text>')
    for e in range(-lim, lim + 1):
        x = cx + e * scale
        label = "1×" if e == 0 else (f"{2 ** e}×" if e > 0 else f"1/{2 ** -e}×")
        s.append(f'<line x1="{x:.1f}" y1="{top - 8}" x2="{x:.1f}" y2="{top + row_h * len(rows)}" stroke="{pal["axis"] if e == 0 else pal["grid"]}" stroke-width="{1.5 if e == 0 else 1}"/>')
        s.append(f'<text x="{x:.1f}" y="{top + row_h * len(rows) + 18}" font-size="11" text-anchor="middle" fill="{pal["axis"]}">{label}</text>')
    for i, (label, r) in enumerate(rows):
        g = geomean(r)
        y = top + i * row_h
        v = math.log2(g)
        tie = abs(g - 1) <= TIE
        color = pal["tie"] if tie else (pal["win"] if g > 1 else pal["loss"])
        w = max(abs(v) * scale, 2)
        x = cx if v >= 0 else cx - w
        s.append(f'<text x="{left - 12}" y="{y + 21}" font-size="13" text-anchor="end" fill="{pal["fg"]}">{escape(label)}</text>')
        s.append(f'<rect x="{x:.1f}" y="{y + 6}" width="{w:.1f}" height="{row_h - 12}" rx="2" fill="{color}"/>')
        # Value labels sit right of the 1x line (clear of the row labels), after the bar when it points right.
        tx = cx + max(v * scale, 0) + 8
        anchor = "start"
        s.append(f'<text x="{tx:.1f}" y="{y + 21}" font-size="12" font-weight="600" text-anchor="{anchor}" fill="{pal["fg"]}">{escape(verdict(g))}</text>')
    s.append("</svg>")
    return "\n".join(s) + "\n"


def splice(text, name, body):
    pat = re.compile(rf"(<!-- begin:{re.escape(name)} -->\n).*?(<!-- end:{re.escape(name)} -->)", re.S)
    if not pat.search(text):
        raise SystemExit(f"docs/performance.md has no block {name}")
    return pat.sub(lambda m: m.group(1) + body + "\n" + m.group(2), text)


def main():
    results = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "bench", "results"))
    p = load_process(results)
    pt = process_tables(p)
    srows = summary_rows(p)
    inproc, data, refused, names, tot = inproc_table(results)

    os.makedirs(IMG, exist_ok=True)
    chart_rows = srows
    for mode in ("light", "dark"):
        with open(os.path.join(IMG, f"oneshader-hero-{mode}.svg"), "w") as f:
            f.write(chart(chart_rows, mode))

    text = open(DOC).read()
    text = splice(text, "summary", summary_table(srows))
    text = splice(text, "process-warm", pt["process-warm"])
    text = splice(text, "process-cold", pt["process-cold"])
    text = splice(text, "size", pt["size"])
    text = splice(text, "inproc", inproc)
    open(DOC, "w").write(text)

    # Numbers the prose quotes, printed for the author.
    print(summary_table(srows))
    print("in-process totals (ms, all files):", {k: round(v / 1e6, 2) for k, v in tot.items()})
    print("refused:", {k: list(v) for k, v in refused.items()})
    print("load:", load_line(p))
    print("host:", p["host"])


if __name__ == "__main__":
    main()
