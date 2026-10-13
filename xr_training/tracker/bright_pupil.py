"""Bright-pupil detector, work in progress (v0, 2026-10-13).

In the Quest 3 mount the two IR LEDs sit next to the camera lens, so the retina
reflects them back and the pupil looks BRIGHT, not dark. Orlosky's tracker looks
for the darkest blob, so it needs this alternative.

Idea: the pupil is a bright elliptical hole inside the darker iris ring.
Known issue: each LED lights one half of the pupil; the ellipse sometimes
covers only one lit half. Next step: fix that, then plug into Orlosky3DEyeTracker.py.
"""

def ring_contrast(g, e):
    (cx,cy),(w,h),a = e
    m = np.zeros_like(g); cv2.ellipse(m, e, 255, -1)
    r = np.zeros_like(g); cv2.ellipse(r, ((cx,cy),(w*1.35,h*1.35),a), 255, -1); r[m>0] = 0
    if (r>0).sum() == 0 or (m>0).sum() == 0: return 0
    return float(g[m>0].mean() - g[r>0].mean())

def track(gray, min_axis=20, max_axis=200):
    """Bright-pupil tracker: the pupil is the bright, elliptical hole inside the darker iris ring.
    Returns (ellipse, confidence) or None."""
    g = cv2.GaussianBlur(gray, (5,5), 0)
    best = None; cands = []
    for T in range(60, 200, 6):
        dark = (g < T).astype(np.uint8) * 255
        dark = cv2.morphologyEx(dark, cv2.MORPH_CLOSE, np.ones((3,3), np.uint8))
        cs, hier = cv2.findContours(dark, cv2.RETR_CCOMP, cv2.CHAIN_APPROX_NONE)
        if hier is None: continue
        for c, hh in zip(cs, hier[0]):
            if hh[3] < 0 or len(c) < 25: continue          # only holes (contours with a parent)
            c = cv2.convexHull(c)                            # glints on the edge leave notches; hull removes them
            if len(c) < 5: continue
            e = cv2.fitEllipse(c)
            w, h = e[1]
            if min(w,h) < min_axis or max(w,h) > max_axis or max(w,h)/min(w,h) > 2.6: continue
            fill = cv2.contourArea(c) / (np.pi*w*h/4)
            if fill < 0.9 or fill > 1.1: continue
            con = ring_contrast(g, e)
            if con < 15: continue
            score = con * fill
            cands.append((score, e, con, w*h))
            if best is None or score > best[0]:
                best = (score, e, con, w*h)
    if best is None: return None
    # Two IR LEDs light the pupil in two lobes with a darker band between them.
    # The best-contrast hole is often one lobe; take the largest clean ellipse that contains it.
    (sx, sy) = best[1][0]; seed_area = best[3]
    def contains(e, x, y):
        (cx,cy),(w,h),a = e; t = np.deg2rad(a); dx, dy = x-cx, y-cy
        u = (dx*np.cos(t)+dy*np.sin(t))/(w/2); v = (-dx*np.sin(t)+dy*np.cos(t))/(h/2)
        return u*u + v*v < 1
    full = [c for c in cands if contains(c[1], sx, sy) and c[2] >= 0.4*best[2] and c[3] <= 5*seed_area]
    pick = max(full, key=lambda c: c[3]) if full else best
    return pick[1], min(1.0, pick[2] / 60)
