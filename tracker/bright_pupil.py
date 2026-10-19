"""Bright-pupil detector for the IR camera inside the Quest 3 (v1, 2026-10-16).

In this mount the two 850 nm LEDs sit right next to the camera lens, so the retina
reflects them back and the pupil looks BRIGHT. The original tracker looks for the darkest
blob, so it needs this alternative.

Each LED lights one half of the pupil, so the pupil shows up as a left and a right lobe
with a darker vertical band between them (sometimes darker than the iris). v0 often
fitted only one lobe. v1 works in two stages:

1. Seed: the pupil (or one lobe) is a bright elliptical hole inside the darker iris
   ring. Sweep thresholds, take holes, also try merging two neighbouring holes
   (the two lobes). This gives an ellipse that is at least one lobe.
2. Refine: cast rays from the seed centre and from points shifted one lobe width to
   the left and right (that is, inside the other lobe). On every ray keep the
   strongest bright-to-dark step, fit an ellipse with RANSAC, and keep the largest fit
   that is well supported by edges and contains the seed. The pupil boundary is a sharp
   step; the band between the lobes is a slow dip, so it rarely wins on a ray.

Usage:
    from bright_pupil import detect
    result = detect(gray)          # None, or (ellipse, confidence)
    ellipse = ((cx, cy), (w, h), angle)   # OpenCV RotatedRect convention
    confidence in [0, 1]: share of the ellipse outline that is a clear pupil edge.
    Treat confidence < 0.5 as "no pupil" (blink, eye out of view).
"""
import cv2
import numpy as np

MIN_AXIS = 30      # px, smallest plausible pupil axis in a 640 px wide frame
MAX_AXIS = 200
_rng = np.random.default_rng(0)  # fixed seed: the same video gives the same result


# ---------------------------------------------------------------- helpers
def _inside(e, x, y):
    (ex, ey), (w, h), a = e
    t = np.deg2rad(a); dx, dy = x - ex, y - ey
    u = (dx*np.cos(t) + dy*np.sin(t)) / (w/2)
    v = (-dx*np.sin(t) + dy*np.cos(t)) / (h/2)
    return u*u + v*v < 1


def _resid(e, P):
    """Approximate distance (px) of points P to the ellipse outline."""
    (ex, ey), (w, h), a = e
    ca, sa = np.cos(np.deg2rad(a)), np.sin(np.deg2rad(a))
    u = ((P[:, 0]-ex)*ca + (P[:, 1]-ey)*sa) / (w/2)
    v = (-(P[:, 0]-ex)*sa + (P[:, 1]-ey)*ca) / (h/2)
    return np.abs(np.sqrt(u*u + v*v) - 1) * min(w, h) / 2


def _plausible(e, min_axis=MIN_AXIS, max_axis=MAX_AXIS, max_ratio=2.6):
    w, h = e[1]
    return min_axis <= min(w, h) and max(w, h) <= max_axis and max(w, h) / max(min(w, h), 1e-3) < max_ratio


def ring_contrast(g, e):
    """Mean brightness inside the ellipse minus mean of a ring just outside it."""
    (cx, cy), (w, h), a = e
    m = np.zeros_like(g); cv2.ellipse(m, e, 255, -1)
    r = np.zeros_like(g); cv2.ellipse(r, ((cx, cy), (w*1.35, h*1.35), a), 255, -1); r[m > 0] = 0
    if (r > 0).sum() == 0 or (m > 0).sum() == 0:
        return 0.0
    return float(g[m > 0].mean() - g[r > 0].mean())


def edge_coverage(gf, e, n=72, off=3.0, dmin=8.0):
    """Share of the outline where it is clearly brighter just inside than just outside."""
    (ex, ey), (w, h), a = e
    t = np.linspace(0, 2*np.pi, n, endpoint=False)
    ca, sa = np.cos(np.deg2rad(a)), np.sin(np.deg2rad(a))
    px, py = (w/2)*np.cos(t), (h/2)*np.sin(t)
    nx, ny = np.cos(t)/(w/2), np.sin(t)/(h/2)
    nn = np.hypot(nx, ny); nx, ny = nx/nn, ny/nn

    def sample(sx, sy):
        X = ex + sx*ca - sy*sa; Y = ey + sx*sa + sy*ca
        return cv2.remap(gf, X.astype(np.float32)[None], Y.astype(np.float32)[None],
                         cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)[0]
    d = sample(px - off*nx, py - off*ny) - sample(px + off*nx, py + off*ny)
    return float((d > dmin).mean())


# ---------------------------------------------------------------- stage 1: seed
def _fit_hull(pts, g, min_axis):
    hull = cv2.convexHull(pts)
    if len(hull) < 5:
        return None
    e = cv2.fitEllipse(hull)
    if not _plausible(e, min_axis=min_axis, max_axis=MAX_AXIS):
        return None
    w, h = e[1]
    fill = cv2.contourArea(hull) / (np.pi*w*h/4)
    if fill < 0.88 or fill > 1.08:
        return None
    con = ring_contrast(g, e)
    if con < 12:
        return None
    return e, con, fill, w*h


def seed(gray):
    """Bright elliptical hole (pupil or one LED lobe) inside the darker iris ring.
    Returns an ellipse or None. Runs at half resolution for speed."""
    k = 2 if gray.shape[1] > 400 else 1
    small = cv2.resize(gray, (gray.shape[1]//k, gray.shape[0]//k), interpolation=cv2.INTER_AREA) if k > 1 else gray
    g = cv2.GaussianBlur(small, (3, 3), 0)
    cands = []
    for T in range(60, 200, 6):
        dark = (g < T).astype(np.uint8) * 255
        dark = cv2.morphologyEx(dark, cv2.MORPH_CLOSE, np.ones((3, 3), np.uint8))
        cs, hier = cv2.findContours(dark, cv2.RETR_CCOMP, cv2.CHAIN_APPROX_NONE)
        if hier is None:
            continue
        holes = {}
        for c, hh in zip(cs, hier[0]):
            if hh[3] >= 0 and len(c) >= 25 // k:          # holes = contours with a parent
                holes.setdefault(hh[3], []).append(c)
        for hs in holes.values():
            hs = sorted(hs, key=cv2.contourArea, reverse=True)[:4]
            groups = [[h] for h in hs]
            for i in range(len(hs)):                 # two neighbouring holes = two lobes
                for j in range(i+1, len(hs)):
                    xi, yi, wi, hi = cv2.boundingRect(hs[i]); xj, yj, wj, hj = cv2.boundingRect(hs[j])
                    gx = max(0, max(xi, xj) - min(xi+wi, xj+wj)); gy = max(0, max(yi, yj) - min(yi+hi, yj+hj))
                    if max(gx, gy) < 0.6*min(max(wi, hi), max(wj, hj)):
                        groups.append([hs[i], hs[j]])
            for grp in groups:
                r = _fit_hull(np.vstack(grp), g, 20 / k)
                if r is not None:
                    e, con, fill, area = r
                    cands.append((con*fill, e, con, area))
    if not cands:
        return None
    best = max(cands, key=lambda c: c[0])
    sx, sy = best[1][0]
    full = [c for c in cands if _inside(c[1], sx, sy) and c[2] >= 0.4*best[2] and c[3] <= 5*best[3]]
    (ex, ey), (w, h), a = (max(full, key=lambda c: c[3]) if full else best)[1]
    return ((ex*k + (k-1)/2, ey*k + (k-1)/2), (w*k, h*k), a)


# ---------------------------------------------------------------- stage 2: refine
def _ray_edges(gf, c, rmin, rmax, n_rays=72, dmin=3.5):
    """Per ray from c: up to 3 local maxima of the outward brightness drop."""
    H, W = gf.shape
    rs = np.arange(rmin, rmax, 1.0)
    out = []
    for k in range(n_rays):
        t = 2*np.pi*k/n_rays
        xs = c[0] + rs*np.cos(t); ys = c[1] + rs*np.sin(t)
        ok = (xs >= 1) & (xs < W-2) & (ys >= 1) & (ys < H-2)
        if ok.sum() < 8:
            continue
        xs, ys = xs[ok], ys[ok]
        prof = cv2.remap(gf, xs.astype(np.float32)[None], ys.astype(np.float32)[None], cv2.INTER_LINEAR)[0]
        d = np.zeros_like(prof); d[2:-2] = (prof[:-4] - prof[4:]) / 4.0
        pk = [i for i in range(2, len(d)-2) if d[i] >= dmin and d[i] == d[i-2:i+3].max()]
        if pk:
            out.append([(xs[i], ys[i], d[i]) for i in sorted(pk, key=lambda i: -d[i])[:3]])
    return out


def _ransac(P, iters=150, tol=2.5):
    best = None
    for _ in range(iters):
        S = P[_rng.choice(len(P), 6, replace=False)]
        try:
            e = cv2.fitEllipse(S)
        except cv2.error:
            continue
        if not _plausible(e, min_axis=20):
            continue
        n = int((_resid(e, P) < tol).sum())
        if best is None or n > best[0]:
            best = (n, e)
    if best is None:
        return None
    inl = P[_resid(best[1], P) < tol]
    return cv2.fitEllipse(inl) if len(inl) >= 6 else best[1]


def refine(gray, s):
    """Grow a lobe seed to the full pupil. Returns (ellipse, confidence)."""
    g = cv2.GaussianBlur(gray, (5, 5), 0)
    # grey opening removes the small corneal glints, which would make strong false edges
    gf = cv2.morphologyEx(g, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (9, 9))).astype(np.float32)
    (cx, cy), (w, h), ang = s
    lobe_w = cv2.boundingRect(cv2.ellipse2Poly((int(cx), int(cy)), (int(w/2), int(h/2)), int(ang), 0, 360, 10))[2]
    rmax = min(MAX_AXIS/2, 2.0*max(w, h))
    cands = []
    for dx in (0.0, -0.8*lobe_w, 0.8*lobe_w):
        rays = _ray_edges(gf, (cx+dx, cy), 0.15*min(w, h), rmax)
        if len(rays) < 12:
            continue
        P = np.array([max(ps, key=lambda p: p[2])[:2] for ps in rays], np.float32)
        e = _ransac(P)
        if e is None or not _plausible(e, min_axis=20) or not _inside(e, cx, cy):
            continue
        inl = float((_resid(e, P) < 2.5).mean())
        cov = edge_coverage(gf, e)
        if inl >= 0.5 and cov >= 0.5 and ring_contrast(g, e) >= 4:
            cands.append((e, cov))
    if not cands:
        return s, edge_coverage(gf, s)
    return max(cands, key=lambda c: c[0][1][0]*c[0][1][1])


# ---------------------------------------------------------------- public
def _detect_full(gray):
    s = seed(gray)
    if s is None:
        return None
    return refine(gray, s)


def detect(gray, prev=None):
    """Find the bright pupil in a grayscale eye image.
    prev: last frame's ellipse (optional). Then a window around it is searched first, which
    keeps the tracker on the same pupil; the full frame is searched if that fails.
    Returns (ellipse, confidence) or None when no plausible pupil is visible."""
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
    if min(w, h) < MIN_AXIS or x - r < 5 or y - r < 5 or x + r > W - 5 or y + r > H - 5:
        return None                     # tiny or cut by the frame edge: skin highlight, not a pupil
    return e, conf


def track(gray):
    """Backwards-compatible name used by v0."""
    return detect(gray)
