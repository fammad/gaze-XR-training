"""Make GIFs from a recorded session folder (run_session.sh output).

    python tools/make_gif.py sessions/<folder> --start 35 --end 50

Writes into the session folder:
  headset_<start>-<end>.gif   left-eye view of the headset (gaze dot, heatmap, scanpath)
  eye_<start>-<end>.gif       eye camera with the pupil ellipse, same moment
  side_<start>-<end>.gif      both side by side, synced on the Mac clock
--start/--end are seconds into headset.mp4. Needs ffmpeg (brew install ffmpeg).
"""
import argparse, bisect, csv, os, subprocess, tempfile
import cv2
import numpy as np

ap = argparse.ArgumentParser()
ap.add_argument("session")
ap.add_argument("--start", type=float, default=0)
ap.add_argument("--end", type=float, default=15)
ap.add_argument("--fps", type=int, default=10)
ap.add_argument("--width", type=int, default=360, help="width of the headset view in the GIF")
ap.add_argument("--headset-delay", type=float, default=1.0,
                help="seconds between script start and the first headset frame (scrcpy startup)")
a = ap.parse_args()

S = a.session
start_time = float(open(os.path.join(S, "start_time.txt")).read())
eye_t = [float(r["mac_time"]) for r in csv.DictReader(open(os.path.join(S, "eye_gaze.csv")))]
hs = cv2.VideoCapture(os.path.join(S, "headset.mp4"))
ey = cv2.VideoCapture(os.path.join(S, "eye.mp4"))
hs_fps = hs.get(cv2.CAP_PROP_FPS)
n_eye = int(ey.get(cv2.CAP_PROP_FRAME_COUNT))


def left_eye(frame):
    """scrcpy shows both eyes; keep the left one and cut the black lens corners."""
    h, w = frame.shape[:2]
    f = frame[:, : w // 2]
    m = int(0.12 * f.shape[1])
    f = f[int(0.08 * h): int(0.92 * h), m: f.shape[1] - m]
    return cv2.resize(f, (a.width, int(a.width * f.shape[0] / f.shape[1])), interpolation=cv2.INTER_AREA)


def eye_at(mac_time):
    i = min(max(bisect.bisect_left(eye_t, mac_time), 0), n_eye - 1)
    ey.set(cv2.CAP_PROP_POS_FRAMES, i)
    ok, f = ey.read()
    return f if ok else None


tmp = tempfile.mkdtemp()
writers = {}
def write(name, img):
    if name not in writers:
        writers[name] = cv2.VideoWriter(os.path.join(tmp, name + ".mp4"), cv2.VideoWriter_fourcc(*"mp4v"),
                                        a.fps, (img.shape[1], img.shape[0]))
    writers[name].write(img)

t = a.start
while t < a.end:
    hs.set(cv2.CAP_PROP_POS_FRAMES, int(t * hs_fps))
    ok, hf = hs.read()
    if not ok:
        break
    h = left_eye(hf)
    e = eye_at(start_time + a.headset_delay + t)
    if e is not None:
        e = cv2.resize(e, (int(h.shape[0] * e.shape[1] / e.shape[0]), h.shape[0]))
        write("eye", e)
        write("side", np.hstack([h, np.full((h.shape[0], 6, 3), 255, np.uint8), e]))
    write("headset", h)
    t += 1.0 / a.fps

for w in writers.values():
    w.release()
tag = f"{a.start:g}-{a.end:g}"
for name in writers:
    src = os.path.join(tmp, name + ".mp4")
    out = os.path.join(S, f"{name}_{tag}.gif")
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", src, "-vf",
                    f"fps={a.fps},split[x][y];[x]palettegen=max_colors=96:stats_mode=diff[p];[y][p]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle",
                    "-loop", "0", out], check=True)
    print(f"{out}  {os.path.getsize(out) / 1e6:.1f} MB")
