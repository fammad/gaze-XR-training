"""Check the bright-pupil detector on a recording. Prints a short summary and writes a contact sheet.

    python xr_training/tools/check_video.py ../test_video.mov
    python xr_training/tools/check_video.py ../test_video.mov --frames 300,540,900,1350

Output: text summary in the terminal (paste it into the chat) and <video>_check.jpg next to the
video (drag it into the chat). --csv also saves one row per frame.
"""
import argparse, os, sys, time
import cv2
import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tracker"))
import bright_pupil as bp

ap = argparse.ArgumentParser()
ap.add_argument("video")
ap.add_argument("--frames", help="comma separated frame numbers for the sheet (default: 16 spread over the video)")
ap.add_argument("--csv", action="store_true", help="also write <video>_check.csv")
args = ap.parse_args()

cap = cv2.VideoCapture(args.video)
fps = cap.get(cv2.CAP_PROP_FPS) or 30
R, prev, t0 = [], None, time.time()
want = None
if args.frames:
    want = set(int(x) for x in args.frames.split(","))
keep = {}
n_total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
if want is None:
    want = set(np.linspace(0, max(n_total - 1, 0), 16).astype(int).tolist())
k = 0
while True:
    ok, f = cap.read()
    if not ok:
        break
    g = cv2.cvtColor(f, cv2.COLOR_BGR2GRAY)
    r = bp.detect(g, prev)
    if r and r[1] >= 0.5:
        (x, y), (w, h), a = r[0]; R.append((x, y, w, h, a, r[1])); prev = r[0]
    else:
        R.append(None); prev = None
    if k in want:
        keep[k] = (g, R[-1])
    k += 1
n = len(R)
ok = np.array([r is not None for r in R])
print(f"{os.path.basename(args.video)}: {n} frames, {n/fps:.1f} s, {1000*(time.time()-t0)/max(n,1):.0f} ms/frame")
print(f"pupil found in {ok.sum()} frames ({100*ok.mean():.0f}%)")
blk = int(5 * fps)
for s in range(0, n, blk):
    E = [r for r in R[s:s+blk] if r]
    if E:
        a = np.array(E)
        print(f"  {s/fps:5.1f}-{min(n, s+blk)/fps:5.1f}s  found {100*len(E)/len(R[s:s+blk]):3.0f}%  "
              f"centre ({np.median(a[:,0]):.0f},{np.median(a[:,1]):.0f})  axes {np.median(np.minimum(a[:,2],a[:,3])):.0f}x{np.median(np.maximum(a[:,2],a[:,3])):.0f}  conf {np.median(a[:,5]):.2f}")
    else:
        print(f"  {s/fps:5.1f}-{min(n, s+blk)/fps:5.1f}s  found   0%")
runs, i = [], 0
while i < n:
    if not ok[i]:
        j = i
        while j < n and not ok[j]:
            j += 1
        if j - i >= int(0.3 * fps):
            runs.append((i, j - i))
        i = j
    else:
        i += 1
print("misses >= 0.3 s (start frame, length):", runs[:30])

tiles = []
for k in sorted(keep):
    g, r = keep[k]
    c = cv2.cvtColor(g, cv2.COLOR_GRAY2BGR)
    if r:
        cv2.ellipse(c, ((r[0], r[1]), (r[2], r[3]), r[4]), (0, 255, 0), 2)
    cv2.putText(c, f"{k}" + ("" if r else " none"), (8, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.9, (0, 0, 255), 2)
    tiles.append(cv2.resize(c, (320, int(320 * c.shape[0] / c.shape[1]))))
if tiles:
    cols = 4
    while len(tiles) % cols:
        tiles.append(np.zeros_like(tiles[0]))
    sheet = np.vstack([np.hstack(tiles[i:i+cols]) for i in range(0, len(tiles), cols)])
    out = os.path.splitext(args.video)[0] + "_check.jpg"
    cv2.imwrite(out, sheet, [cv2.IMWRITE_JPEG_QUALITY, 85])
    print("sheet:", out)
if args.csv:
    out = os.path.splitext(args.video)[0] + "_check.csv"
    with open(out, "w") as fh:
        fh.write("frame,cx,cy,w,h,angle,confidence\n")
        for k, r in enumerate(R):
            fh.write(f"{k}," + (",".join(f"{v:.2f}" for v in r) if r else ",,,,,") + "\n")
    print("csv:", out)
