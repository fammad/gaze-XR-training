"""Mixed-pupil detector (2026-10-19).

With the LEDs a bit off the lens axis the pupil is neither fully bright nor fully dark:
one or two bright lobes plus a dark band, and the mix changes as the eye moves.
The only stable thing is the pupil OUTLINE: a sharp step against the iris, brighter or
darker depending on where you are on it.

1. Seed: a bright lobe (bright_pupil.seed) or, if there is none, the darkest blob.
2. Cast rays from the seed and from 4 shifted points, keep up to 3 sharp steps per ray
   in either direction (|gradient|), pool them.
3. RANSAC ellipse over the pooled points. The pupil outline is shared by all rays, the
   inner lobe/band edges are not elliptical, so the outline wins.

detect(gray, prev=None) -> (ellipse, confidence) | None, same API as bright_pupil.
"""
import cv2
import numpy as np
import bright_pupil as bp

_rng = np.random.default_rng(0)
MAX_RATIO = 3.0   # side gaze makes the pupil a flat ellipse


def _filtered(gray):
    g = cv2.GaussianBlur(gray, (5, 5), 0)
    g = cv2.morphologyEx(g, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (9, 9)))   # glints
    g = cv2.morphologyEx(g, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5)))  # thin lashes
    return g.astype(np.float32)


def _dark_seeds(gray, max_seeds=3):
    """Compact dark blobs away from the frame border (pupil candidates, not lashes)."""
    g = cv2.GaussianBlur(gray, (9, 9), 0)
    g = cv2.morphologyEx(g, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (7, 7)))  # erase lashes
    H, W = g.shape
    lo = float(np.percentile(g, 0.5))
    out = []
    for add in (15, 30, 45):
        m = (g < lo + add).astype(np.uint8) * 255
        cs, _ = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
        for c in cs:
            if len(c) < 5 or cv2.contourArea(c) < 150:
                continue
            x, y, w, h = cv2.boundingRect(c)
            if x <= 2 or y <= 2 or x + w >= W - 2 or y + h >= H - 2:
                continue
            e = cv2.fitEllipse(c)
            area_e = np.pi * e[1][0] * e[1][1] / 4
            if area_e <= 0 or cv2.contourArea(c) / area_e < 0.7:
                continue
            out.append((cv2.contourArea(c), e))
    out.sort(key=lambda z: -z[0])
    return [e for _, e in out[:max_seeds]]


def _ray_points(gf, c, rmax, n_rays=72, dmin=4.0, top=3):
    H, W = gf.shape
    rs = np.arange(4.0, rmax, 1.0)
    P = []
    for k in range(n_rays):
        t = 2 * np.pi * k / n_rays
        xs = c[0] + rs * np.cos(t); ys = c[1] + rs * np.sin(t)
        ok = (xs >= 1) & (xs < W - 2) & (ys >= 1) & (ys < H - 2)
        if ok.sum() < 8:
            continue
        xs, ys = xs[ok], ys[ok]
        prof = cv2.remap(gf, xs.astype(np.float32)[None], ys.astype(np.float32)[None], cv2.INTER_LINEAR)[0]
        d = np.zeros_like(prof); d[2:-2] = np.abs(prof[:-4] - prof[4:]) / 4.0
        pk = [i for i in range(2, len(d) - 2) if d[i] >= dmin and d[i] == d[i - 2:i + 3].max()]
        for i in sorted(pk, key=lambda i: -d[i])[:top]:
            P.append((xs[i], ys[i]))
    return P


def coverage(gf, e, n=72, off=3.0, dmin=8.0):
    """Share of the outline with a clear step (either direction) across it."""
    (ex, ey), (w, h), a = e
    t = np.linspace(0, 2 * np.pi, n, endpoint=False)
    ca, sa = np.cos(np.deg2rad(a)), np.sin(np.deg2rad(a))
    px, py = (w / 2) * np.cos(t), (h / 2) * np.sin(t)
    nx, ny = np.cos(t) / (w / 2), np.sin(t) / (h / 2)
    nn = np.hypot(nx, ny); nx, ny = nx / nn, ny / nn

    def sample(sx, sy):
        X = ex + sx * ca - sy * sa; Y = ey + sx * sa + sy * ca
        return cv2.remap(gf, X.astype(np.float32)[None], Y.astype(np.float32)[None],
                         cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)[0]
    return float((np.abs(sample(px - off * nx, py - off * ny) - sample(px + off * nx, py + off * ny)) > dmin).mean())


def _ransac(P, must, iters=400, tol=2.0):
    best = None
    for _ in range(iters):
        S = P[_rng.choice(len(P), 6, replace=False)]
        try:
            e = cv2.fitEllipse(S)
        except cv2.error:
            continue
        if not bp._plausible(e, max_ratio=MAX_RATIO) or not bp._inside(e, *must):
            continue
        n = int((bp._resid(e, P) < tol).sum())
        if best is None or n > best[0]:
            best = (n, e)
    if best is None:
        return None
    inl = P[bp._resid(best[1], P) < tol]
    return cv2.fitEllipse(inl) if len(inl) >= 6 else best[1]


def _fit_from_seed(gray, gf, s):
    (cx, cy), (w, h), _ = s
    L = min(max(w, h), bp.MAX_AXIS)
    P = []
    for c in [(cx, cy), (cx - 0.8 * L, cy), (cx + 0.8 * L, cy), (cx, cy - 0.5 * L), (cx, cy + 0.5 * L)]:
        P += _ray_points(gf, c, bp.MAX_AXIS / 2)
    if len(P) < 20:
        return None
    e = _ransac(np.array(P, np.float32), (cx, cy))
    if e is None or not bp._plausible(e, min_axis=bp.MIN_AXIS, max_ratio=MAX_RATIO):
        return None   # the inlier refit can collapse to a thin sliver
    return e, coverage(gf, e)


def _detect_full(gray):
    global _rng
    _rng = np.random.default_rng(0)   # same frame, same answer
    seeds = []
    b = bp.seed(gray)
    if b is not None:
        seeds.append(b)
    seeds += _dark_seeds(gray)
    if not seeds:
        return None
    gf = _filtered(gray)
    best = None
    for s in seeds:
        r = _fit_from_seed(gray, gf, s)
        if r is not None and (best is None or r[1] > best[1]):
            best = r
    return best


def detect(gray, prev=None):
    H, W = gray.shape
    res = None
    if prev is not None:
        (px, py), (pw, ph), _ = prev
        half = int(1.6 * max(pw, ph)) + 20
        x0, y0 = max(0, int(px) - half), max(0, int(py) - half)
        x1, y1 = min(W, int(px) + half), min(H, int(py) + half)
        if x1 - x0 > 60 and y1 - y0 > 60:
            r = _detect_full(gray[y0:y1, x0:x1])
            if r is not None and r[1] >= 0.5:
                (ex, ey), axes, a = r[0]
                res = (((ex + x0, ey + y0), axes, a), r[1])
    if res is None:
        res = _detect_full(gray)
    if res is None:
        return None
    e, conf = res
    (x, y), (w, h), _ = e
    r = max(w, h) / 2
    if min(w, h) < bp.MIN_AXIS or x - r < 5 or y - r < 5 or x + r > W - 5 or y + r > H - 5:
        return None
    return e, conf
