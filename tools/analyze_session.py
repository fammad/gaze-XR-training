"""Turn a headset session log into the numbers and figures a trainer would look at.

    python tools/analyze_session.py <session_*.csv> [--out docs/analysis] [--phase task|all]

Reads the CSV written by GazeLogger on the Quest and writes:
  dwell.png        fixation time per object group (where the eyes went)
  timeline.png     fixations over time, one lane per object group, with grab/release events
  eye_vs_head.png  angle between eye gaze and head direction (is the head a good proxy?)
  summary.md       the same numbers as a table, ready to paste into a README

Objects are grouped by name (see GROUPS). Tag your own scene with GazeAOI to get exact
categories in the log; this script falls back to names so it also works on older logs.
"""
import argparse, collections, csv, math, os, re

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

# ---- object groups, first match wins (lower-case regex on the collider name)
GROUPS = [
    ("Tool", r"hammer|saw|chisel|file|screwdriver|wrench|tool$"),
    ("Material", r"log|metal|clay|rock|wood|marble|stone|bendable"),
    ("Instructions", r"board|sign|instruction|chalk|screen|panel"),
    ("Workbench and stands", r"workbench|bench|stand|table|desk"),
    ("Room (walls, floor)", r"wall|plane|floor|ceiling|sky"),
]
SELF = re.compile(r"xr origin|hand|controller|player", re.I)   # gaze hitting your own body: ignored
ORDER = [g for g, _ in GROUPS]

# ---- reference palette (light), categorical slots in fixed order + text tokens
SURFACE, INK, INK2, GRID = "#fcfcfb", "#0b0b0b", "#52514e", "#e4e3df"
SLOTS = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#6250d6"]
COLOR = dict(zip(ORDER, SLOTS))


def group_of(name):
    for g, rx in GROUPS:
        if re.search(rx, name, re.I):
            return g
    return "Other"


def style(ax):
    ax.set_facecolor(SURFACE)
    for s in ("top", "right", "left"):
        ax.spines[s].set_visible(False)
    ax.spines["bottom"].set_color(GRID)
    ax.tick_params(colors=INK2, labelsize=11, length=0)
    ax.xaxis.label.set_color(INK2)
    ax.grid(axis="x", color=GRID, linewidth=0.8)
    ax.set_axisbelow(True)


def load(path):
    rows = list(csv.DictReader(open(path)))
    events = []
    for r in rows:
        for e in r["events"].split(";"):
            if e:
                events.append((float(r["time"]), e))
    return rows, events


def windows(events, start_key, end_keys):
    out, t0 = [], None
    for t, e in events:
        if e.startswith(start_key) and t0 is None:
            t0 = t
        elif t0 is not None and any(e.startswith(k) for k in end_keys):
            out.append((t0, t)); t0 = None
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("log")
    ap.add_argument("--out", default="docs/analysis")
    ap.add_argument("--phase", choices=["task", "all"], default="task")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    rows, events = load(a.log)
    t_end = float(rows[-1]["time"])

    # parts of the session that are not real looking behaviour: calibration and accuracy grid
    skip = windows(events, "calibration_start", ["calibrated", "calibration_failed"])
    skip += windows(events, "grid_start", ["grid_end"])
    if a.phase == "task":
        # the task runs from "phase:task" (SessionRunner) or the first task_start,
        # to the first task_end / post-task grid / session end after it
        starts = [t for t, e in events if e == "phase:task"] or [t for t, e in events if e == "task_start"]
        t0 = starts[0] if starts else float(rows[0]["time"])
        ends = [t for t, e in events if t > t0 and (e.startswith("task_end") or e in ("phase:grid_post", "session_end"))]
        t1 = ends[0] if ends else t_end
    else:
        t0, t1 = float(rows[0]["time"]), t_end
    def keep(t):
        return t0 <= t <= t1 and not any(s <= t <= e for s, e in skip)

    # ---- fixations (logged at their end with duration in ms)
    fix = []
    for t, e in events:
        if e.startswith("fix:"):
            name, ms = e[4:].rsplit(":", 1)
            d = float(ms) / 1000
            if keep(t) and not SELF.search(name):
                fix.append((t - d, d, name, group_of(name)))
    dwell = collections.defaultdict(float); count = collections.Counter()
    for s, d, n, g in fix:
        dwell[g] += d; count[g] += 1
    groups = [g for g in ORDER + ["Other"] if dwell.get(g)]
    total_fix = sum(dwell.values())

    # ---- data quality and eye vs head
    task_rows = [r for r in rows if keep(float(r["time"]))]
    tracked = [r for r in task_rows if r["gaze_source"] == "Tracker" and r["calibrated"] == "1"]
    seqs = {r["seq"] for r in tracked}
    dur = sum(1 for _ in task_rows) and (float(task_rows[-1]["time"]) - float(task_rows[0]["time"]))
    ang = []
    for r in tracked:
        h = [float(r[k]) for k in ("head_fx", "head_fy", "head_fz")]
        v = [float(r[k]) for k in ("eye_dx", "eye_dy", "eye_dz")]
        c = sum(x * y for x, y in zip(h, v)) / math.sqrt(sum(x * x for x in h) * sum(y * y for y in v))
        ang.append(math.degrees(math.acos(max(-1.0, min(1.0, c)))))
    ang.sort()
    n = len(ang) or 1
    pct = lambda f: 100 * sum(1 for x in ang if f(x)) / n
    q = lambda p: ang[min(int(p * n), n - 1)] if ang else float("nan")

    plt.rcParams.update({"font.family": "DejaVu Sans", "figure.facecolor": SURFACE})

    # ---- 1. dwell per group
    fig, ax = plt.subplots(figsize=(9, 0.6 * len(groups) + 1.4), dpi=160)
    style(ax)
    ys = list(range(len(groups)))[::-1]
    vals = [dwell[g] for g in groups]
    ax.barh(ys, vals, color=[COLOR.get(g, "#8a8984") for g in groups], height=0.6)
    for y, g, v in zip(ys, groups, vals):
        ax.text(v + total_fix * 0.01, y, f"{v:.1f} s  ({100 * v / total_fix:.0f} %, {count[g]} fixation{'s' if count[g] != 1 else ''})",
                va="center", color=INK, fontsize=11)
    ax.set_yticks(ys, groups, color=INK, fontsize=12)
    ax.set_xlim(0, max(vals) * 1.45)
    ax.set_xlabel("fixation time (s)")
    ax.set_title("Where the eyes went during the task", loc="left", color=INK, fontsize=14, pad=12)
    fig.tight_layout(); fig.savefig(os.path.join(a.out, "dwell.png")); plt.close(fig)

    # ---- 2. timeline
    fig, ax = plt.subplots(figsize=(11, 0.55 * len(groups) + 1.6), dpi=160)
    style(ax)
    lane = {g: i for i, g in enumerate(groups[::-1])}
    for s, d, nme, g in fix:
        ax.broken_barh([(s - t0, max(d, 0.08))], (lane[g] - 0.32, 0.64), color=COLOR.get(g, "#8a8984"))
    last_label = -1e9
    for t, e in events:
        if (e.startswith("grab:") or e.startswith("release:")) and t0 <= t <= t1:
            ax.axvline(t - t0, color=INK2, linewidth=0.8, linestyle=(0, (2, 3)))
            if e.startswith("grab:") and t - last_label > 0.07 * (t1 - t0):   # skip labels that would overlap
                ax.text(t - t0 + 0.3, len(groups) - 0.3, e.split(":")[1], fontsize=9, color=INK2, ha="left")
                last_label = t
    for t, e in events:
        if e.startswith("rule_violation") and t0 <= t <= t1:
            ax.plot([t - t0], [len(groups) - 0.55], marker="v", color="#e34948", markersize=9)
    viol = [t for t, e in events if e.startswith("rule_violation") and t0 <= t <= t1]
    if viol:
        ax.text(viol[-1] - t0 + 1.5, len(groups) - 0.6, "rule broken: " + [e for t, e in events if e.startswith("rule_violation")][0].split(":", 1)[1].replace("_", " "),
                fontsize=9, color="#b3261e", va="center")
    for s, e in skip:
        if e > t0 and s < t1:
            ax.axvspan(max(s, t0) - t0, min(e, t1) - t0, color=GRID, alpha=0.6, linewidth=0)
    ax.set_yticks(list(lane.values()), list(lane.keys()), color=INK, fontsize=11)
    ax.set_xlim(0, t1 - t0); ax.set_ylim(-0.6, len(groups) + 0.1)
    ax.set_xlabel("time in task (s)   ·   dashed: grab / release   ·   red: rule broken   ·   grey: not counted")
    ax.set_title("Fixations over time", loc="left", color=INK, fontsize=14, pad=12)
    fig.tight_layout(); fig.savefig(os.path.join(a.out, "timeline.png")); plt.close(fig)

    # ---- 3. eye vs head
    fig, ax = plt.subplots(figsize=(9, 3.6), dpi=160)
    style(ax); ax.grid(axis="y", color=GRID, linewidth=0.8); ax.grid(axis="x", visible=False)
    ax.hist([x for x in ang if x <= 90], bins=45, range=(0, 90), color=SLOTS[0], rwidth=0.88)
    for x, txt in ((10, "eye ≈ head"), (40, "beyond normal eye rotation")):
        ax.axvline(x, color=INK2, linewidth=1, linestyle=(0, (3, 3)))
        ax.text(x + 1, ax.get_ylim()[1] * 0.92, txt, color=INK2, fontsize=10)
    ax.set_xlabel("angle between eye gaze and head direction (°)")
    ax.set_yticks([])
    ax.set_title(f"Eye vs head direction: median {q(0.5):.0f}°, {pct(lambda x: x > 10):.0f} % of samples more than 10° apart",
                 loc="left", color=INK, fontsize=13, pad=12)
    fig.tight_layout(); fig.savefig(os.path.join(a.out, "eye_vs_head.png")); plt.close(fig)

    # ---- summary
    lines = [
        f"| Session | `{os.path.basename(a.log)}`, {a.phase} phase, {t1 - t0:.0f} s |",
        "|---|---|",
        f"| Gaze samples reaching the headset | {len(seqs) / max(dur, 1e-6):.1f} per second |",
        f"| Fixations | {len(fix)}, {total_fix:.0f} s in total |",
    ]
    for g in groups:
        lines.append(f"| Fixation time on {g.lower()} | {dwell[g]:.1f} s ({100 * dwell[g] / total_fix:.0f} %) |")
    lines += [
        f"| Eye vs head angle | median {q(0.5):.1f}°, 75th percentile {q(0.75):.1f}° |",
        f"| Samples with eye and head more than 10° apart | {pct(lambda x: x > 10):.0f} % |",
        f"| Samples beyond 40° (treated as tracking errors) | {pct(lambda x: x > 40):.0f} % |",
    ]
    open(os.path.join(a.out, "summary.md"), "w").write("\n".join(lines) + "\n")
    print("\n".join(lines))
    print(f"\nFigures in {a.out}/")


if __name__ == "__main__":
    main()
