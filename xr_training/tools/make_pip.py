"""Headset video in full, eye camera as a small picture-in-picture, synced on the clock.

Record the headset with its own recorder (Meta button > Camera > Record video), pull it, then:

    python xr_training/tools/make_pip.py sessions/<folder> <quest_video.mp4> --start 30 --end 60

--start/--end are seconds into the Quest video. Output next to the session: pip_<start>-<end>.mp4
(add --gif for a GIF too). Sync: the Quest file name holds its start time
(..._20261019-222409.mp4, headset clock). If the eye looks a bit early or late, nudge it with
--offset (seconds, + = later eye frames). Needs ffmpeg for --gif.
"""
import argparse, bisect, csv, datetime, os, re, subprocess
import cv2
import numpy as np

ap = argparse.ArgumentParser()
ap.add_argument("session")
ap.add_argument("quest_video")
ap.add_argument("--start", type=float, default=0)
ap.add_argument("--end", type=float, default=None)
ap.add_argument("--offset", type=float, default=0.0)
ap.add_argument("--quest-start", help="override the Quest video start, e.g. 2026-10-19 22:24:09")
ap.add_argument("--inset", type=float, default=0.28, help="eye inset width as a share of the frame width")
ap.add_argument("--corner", choices=["br", "bl", "tr", "tl"], default="br")
ap.add_argument("--gif", action="store_true")
ap.add_argument("--gif-width", type=int, default=720)
a = ap.parse_args()

# ---- when did the Quest video start (Unix time)?
if a.quest_start:
    q0 = datetime.datetime.fromisoformat(a.quest_start).timestamp()
else:
    m = re.search(r"(\d{8})[-_](\d{6})", os.path.basename(a.quest_video))
    if not m:
        raise SystemExit("No date-time in the file name; pass --quest-start 'YYYY-MM-DD HH:MM:SS'")
    q0 = datetime.datetime.strptime(m.group(1) + m.group(2), "%Y%m%d%H%M%S").timestamp()

eye_rows = list(csv.DictReader(open(os.path.join(a.session, "eye_gaze.csv"))))
eye_t = [float(r["mac_time"]) for r in eye_rows]
ey = cv2.VideoCapture(os.path.join(a.session, "eye.mp4"))
n_eye = int(ey.get(cv2.CAP_PROP_FRAME_COUNT))
qv = cv2.VideoCapture(a.quest_video)
fps = qv.get(cv2.CAP_PROP_FPS) or 30
W, H = int(qv.get(cv2.CAP_PROP_FRAME_WIDTH)), int(qv.get(cv2.CAP_PROP_FRAME_HEIGHT))
end = a.end if a.end is not None else qv.get(cv2.CAP_PROP_FRAME_COUNT) / fps
print(f"Quest video {W}x{H} @ {fps:.0f} fps, starts {datetime.datetime.fromtimestamp(q0)}; "
      f"eye log {datetime.datetime.fromtimestamp(eye_t[0])} to {datetime.datetime.fromtimestamp(eye_t[-1])}")

iw = int(W * a.inset); ih = int(iw * 480 / 640); pad = int(0.02 * W); b = 3
x0 = pad if a.corner in ("bl", "tl") else W - iw - pad
y0 = pad if a.corner in ("tl", "tr") else H - ih - pad

tag = f"{a.start:g}-{end:g}"
out = os.path.join(a.session, f"pip_{tag}.mp4")
vw = cv2.VideoWriter(out, cv2.VideoWriter_fourcc(*"mp4v"), fps, (W, H))
qv.set(cv2.CAP_PROP_POS_FRAMES, int(a.start * fps))
last_i, eye_img, missing = -1, None, 0
for k in range(int((end - a.start) * fps)):
    ok, f = qv.read()
    if not ok:
        break
    t = q0 + a.start + k / fps + a.offset
    i = bisect.bisect_left(eye_t, t)
    if 0 < i < len(eye_t) and abs(eye_t[min(i, len(eye_t) - 1)] - t) < 0.5:
        i = min(i, n_eye - 1)
        if i != last_i:
            if i != last_i + 1:
                ey.set(cv2.CAP_PROP_POS_FRAMES, i)
            ok2, e = ey.read()
            if ok2:
                eye_img = cv2.resize(e, (iw, ih), interpolation=cv2.INTER_AREA)
            last_i = i
        f[y0 - b:y0 + ih + b, x0 - b:x0 + iw + b] = 255
        f[y0:y0 + ih, x0:x0 + iw] = eye_img
    else:
        missing += 1
    vw.write(f)
vw.release()
print(f"{out}" + (f"  ({missing} frames had no eye video at that time: check --offset / --quest-start)" if missing else ""))

if a.gif:
    g = out.replace(".mp4", ".gif")
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", out, "-vf",
                    f"fps=12,scale={a.gif_width}:-1:flags=lanczos,split[x][y];[x]palettegen=max_colors=128:stats_mode=diff[p];"
                    "[y][p]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle", "-loop", "0", g], check=True)
    print(f"{g}  {os.path.getsize(g) / 1e6:.1f} MB")
