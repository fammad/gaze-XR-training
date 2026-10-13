# Gaze XR Training: Project Plan

DIY eye tracking on a Meta Quest 3, used to measure attention in a VR workstation training task.
Built for the Volvo Group MSc thesis "Eye tracking in XR Training" (apply by **Oct 30, 2026**, target submit **Oct 28**).

Owner: Fuad Mammadov. Helper: Claude (writes code, you run hardware, Unity and builds).
Budget: about 20 hours over Oct 14 to Oct 28.

---

## 0. For a new chat: read the handover first

Before working on this project in a new chat, read `HANDOVER.md` (in the local project folder `01_eye_tracking_in_VR/`, outside the repo, and as the Project doc `claude/gaze-xr-training_HANDOVER.md`). It has the current state, decisions and their reasons, open issues and working rules.
Update the handover at the end of every work session.

### Changes since the first version of this plan (2026-10-13)
- One repo for everything: the Unity project lives in `unity/` (slimmed to what Playground.unity uses). No symlink, no second repo.
- Orlosky's git history removed; his code is imported as one snapshot commit. His MIT copyright stays in LICENSE.
- Camera mounted **above** the left lens (not below), hot glued, LEDs diffused. Step 4 is done.
- In the headset the pupil appears **bright** (IR retro-reflection). Orlosky's dark-pupil search does not work as is; a bright-pupil detector is in progress (`xr_training/tracker/bright_pupil.py`).
- Controller input goes through Unity XR input (the project uses OpenXR + XR Interaction Toolkit, not the Meta XR Core SDK, so no OVRInput).

## 1. What we are building (in one picture)

```
 GC0308 IR camera (left eye, inside Quest 3)
        | USB cable
        v
 MacBook (Python)  Orlosky pupil tracker -> 3D gaze vector
        | UDP over Wi-Fi (~ every frame)
        v
 Quest 3 (Unity standalone app, TacTool workstation)
   calibration -> gaze ray -> which object you look at (AOI)
        | CSV log on headset
        v
 MacBook (Python analysis) -> accuracy, training metrics, eye vs head gaze, figures
```

Words you will see:
- **AOI** (area of interest): an object we care about, like a hammer or a safety sign. We log when gaze hits it.
- **Fixation**: gaze stays still on one spot for at least ~100 ms. That is when people actually take in information.
- **Dwell time**: total time gaze spent on one AOI.
- **Angular error**: how far (in degrees) the measured gaze is from where you really looked. Lower is better.
- **Head gaze**: just the direction your head points. This is what VR trainings use when they have no eye tracker. We compare against it.

Why this matches the Volvo posting:
- They ask which gaze metrics are **reliable enough** for training. We measure our own tracker's accuracy and show which AOI sizes and metrics survive that error.
- They ask what trainees **notice or overlook**. Our task logs whether you looked at the instruction and safety info before acting.
- They ask **when eye tracking is useful**. We compare eye gaze against head gaze on the same session.
- They want a **blueprint** and a **proof of concept with users**. We ship a one-page draft blueprint built on our own data, plus a working demo on Quest 3.
- Bonus: the Quest 3 has no eye tracking built in. We add it for about 20 EUR.

---

## 2. Decisions made

| Topic | Decision |
|---|---|
| Time | ~20 h, submit Oct 28 |
| Scene | TacTool Scene 2 (Unity 6 + Meta XR SDK, Quest 3). Team agreed it can be used, improved and published. Credit the team in README |
| Code base | Orlosky's JEOresearch/EyeTracker (MIT), pushed with full history into github.com/fammad/gaze-XR-training. We modify his files and commit every change. Credit him in README + references |
| Unity project | Inside gaze-XR-training at `unity/`. Slimmed to what Playground.unity actually uses: 115 MB instead of 1.7 GB. Full TacTool clone stays in `01_eye_tracking_in_VR/unity/` as backup, outside the repo |
| Unity version | 6000.4.5f1, OpenXR + XR Interaction Toolkit 3.4.1 |
| Cameras | Mono, left eye |
| Mac only | Python on Mac, Unity builds to Quest over USB, gaze sent over Wi-Fi (no Quest Link on Mac) |
| Participants | Only you (pilot, N=1, 3 sessions). IR near the eye: self-only, see safety note |

### Still open
1. Does TacTool open and play Playground.unity on your Mac? [Check in Step 3]

---

## 3. Repo layout (target)

```
gaze-XR-training/                 github.com/fammad/gaze-XR-training (his history + your commits)
  README.md                       rewritten: your project, credits Orlosky at top
  LICENSE                         MIT, keeps his copyright line, adds yours
  3DTracker/                      HIS tracker, modified: macOS camera + UDP output
  VREyeTracker/                   HIS Unity scripts, left untouched as reference
  xr_training/                    EVERYTHING NEW IS HERE
    (Unity scripts live in unity/Assets/GazeXR, see below)
      GazeUdpReceiver.cs          receives gaze from the Mac
      MonoGazeTracker.cs          his EyeTracker.cs, adapted (controller buttons, 5-point calib, accuracy grid)
      GazeAOI.cs                  tag any object as an AOI
      GazeLogger.cs               writes one CSV row per frame
      TrainingTask.cs             task steps + events (tool picked, task done)
    tools/udp_listen.py           test tool: print packets on the Mac
    analysis/                     metrics.py, figures.py
    data/sessions/                raw CSV from your 3 sessions
    results/                      figures + tables used in README
    docs/                         PLAN.md, hardware.md (photos), safety.md, BLUEPRINT.md
  unity/                          slim TacTool project (Unity 6000.4.5f1)
    Assets/GazeXR/                our C# scripts:
      GazeUdpReceiver.cs          receives gaze from the Mac
      MonoGazeTracker.cs          his EyeTracker.cs, adapted (controller buttons, 5-point calib, accuracy grid)
      GazeAOI.cs                  tag any object as an AOI
      GazeLogger.cs               writes one CSV row per frame
      TrainingTask.cs             task steps + events
  media/                          demo GIF, mount photos
```

One repo holds everything. Open `gaze-XR-training/unity` in Unity Hub, never the backup clone.

---

## 4. Steps

Each step says what you do, what Claude does, what changes, and the commits.
Rule: commit after each working change. Small commits look professional and make it easy to undo.

### Step 0: Repo + Python setup (Oct 14, ~1 h): You

You already made the empty repo github.com/fammad/gaze-XR-training. We fill it with Orlosky's code including his full commit history, so anyone can see exactly what is his and what is yours.

1. In Terminal:
```bash
cd ~/Desktop/"Personal Project"/01_eye_tracking_in_VR
git clone https://github.com/JEOresearch/EyeTracker.git gaze-XR-training
cd gaze-XR-training
git remote rename origin upstream          # his repo becomes "upstream"
git remote add origin https://github.com/fammad/gaze-XR-training.git
```
Then push with GitHub Desktop: File -> Add Local Repository -> pick `gaze-XR-training` -> **Publish/Push origin**. It handles your GitHub login.
If the push is rejected, your new repo has a README or license commit. In Terminal: `git push -u origin main --force` (safe, the repo is empty).

2. Python:
```bash
brew install python@3.12 python-tk@3.12     # tkinter is needed for his window
python3.12 -m venv .venv && source .venv/bin/activate
pip install opencv-python numpy pandas matplotlib
mkdir -p xr_training/docs && mv ../PLAN.md xr_training/docs/   # plan goes into the repo
```
3. Claude adds `requirements.txt` and `.gitignore` (ignores `.venv`, `gaze_vector.txt`, `.DS_Store`).

Commits:
- `docs: add project plan`
- `chore: add requirements and gitignore`

Done when: `git log` shows your commits on top of his history, and they are pushed (`git push`).

### Step 1: Run his tracker on the Mac (Oct 14 to 15, ~2 h): Claude codes, you test

What changes in `3DTracker/Orlosky3DEyeTracker.py`:
- Camera backend `cv2.CAP_MSMF` (Windows only) becomes `cv2.CAP_AVFOUNDATION` on macOS.
- The video file picker filter `"*.mp4;*.avi"` greys out files on macOS. Fixed.
- The hard-coded upside-down flip becomes an option, since your mount angle decides it.

You test:
1. `cd 3DTracker && python Orlosky3DEyeTracker.py` -> **Browse Video** -> pick `../eye_test.mp4` (his sample). You should see the pupil ellipse and gaze line.
2. System Settings -> Privacy -> Camera -> allow Terminal.
3. Plug in the GC0308, **Start Camera**. Index 0 is usually the FaceTime camera, so try 1.
4. Unscrew the lens a little until your eye is sharp at about 3 cm.

Commits:
- `feat(tracker): macOS camera backend (AVFoundation)`
- `fix(tracker): video file picker on macOS`
- `feat(tracker): optional image flip`

**Gate G1 (Oct 15):** stable pupil ellipse on your real eye with the camera held by hand. If not, we tune thresholds before touching Unity.

### Step 2: Stream gaze over Wi-Fi (Oct 15, ~1 h): Claude codes, you test

His Unity script reads a text file on the same PC. Our Unity app runs on the Quest, so the Mac sends each gaze sample as a small UDP packet instead.

Packet: `seq, mac_time, origin xyz, direction xyz, confidence`.
Confidence (his pupil fit quality) lets us drop bad frames and blinks later.

You test: run `python xr_training/tools/udp_listen.py` in a second Terminal and watch packets arrive while the tracker runs.

Network: use home Wi-Fi or your phone hotspot. Eduroam blocks device-to-device traffic. Find the Quest IP in Quest Settings -> Wi-Fi -> your network.

Commits:
- `feat(tracker): send gaze over UDP with seq, timestamp, confidence`
- `feat(tools): UDP listener for testing`

### Step 3: Gaze in the Quest (Oct 16 to 18, ~3 h): Claude codes, you build

What you prepare:
1. Unity Hub -> your Unity 6 version -> Add modules -> **Android Build Support** (with OpenJDK and SDK).
2. Quest in **Developer Mode** (Meta Horizon app on phone -> Devices -> Developer Mode). Connect with USB-C, accept "Allow USB debugging" in the headset.
3. Unity Hub -> Add -> pick `gaze-XR-training/unity`. First import takes 10 to 20 min.
4. Open `Assets/Scenes/Playground.unity`, press Play. Check: no pink materials, no "missing script" warnings in the Console. If something is missing, tell Claude, it gets copied from the backup.
5. Commit the project in GitHub Desktop.
6. Player Settings -> Android -> Internet Access = **Require**.

What Claude writes:
- `GazeUdpReceiver.cs`: listens on port 5005 in a background thread.
- `MonoGazeTracker.cs`: starts as an exact copy of his `EyeTracker.cs` (first commit), then:
  - keyboard keys -> Quest controller buttons (A = calibrate, B = accuracy grid) via Unity XR input, because there is no keyboard in a standalone headset;
  - his 3-point vertical calibration -> 5-point (up, right, down, left, center), ported from his stereo script, so horizontal gaze is calibrated too;
  - gaze origin moved 3.2 cm to the left of head center (left eye).

You test: add `MonoGazeTracker` to the CenterEyeAnchor of the camera rig, Build and Run, look around, press A to calibrate.

Commits:
- `chore(unity): add slim TacTool project (DH2670 team, used with permission)`
- `chore(unity): copy Orlosky EyeTracker.cs as MonoGazeTracker base`
- `feat(unity): UDP gaze receiver`
- `feat(unity): controller input instead of keyboard`
- `feat(unity): 5-point calibration for mono tracker`

**Gate G2 (Oct 18):** the gaze sphere follows your eye in the headset after calibration.
Tip: test logic in the Unity Editor first with the mouse as fake gaze. Builds take minutes.

### Step 4: Mount the camera in the headset (Oct 19, ~2 h): You

1. Remove the facial interface foam on the left side.
2. Fix the camera below the left lens, on the nose side, pointing up at the eye. Soft wire or tape. Keep it adjustable.
3. Route the cable out under the facial interface. Use the USB extension. You stay seated or standing at the desk.
4. Do not fully enclose the camera, it gets warm.
5. Take photos: camera alone, mounted, worn, and a screenshot of the eye image. These go into `docs/hardware.md`.

Expect stray glints from the lens surfaces. If the pupil fit is unstable, tilt the camera a bit or tape over one IR LED.

Commit: `docs: hardware build notes and photos`

### Step 5: Accuracy test + logging (Oct 20 to 21, ~2.5 h): Claude codes, you test

- **Accuracy grid:** 5x5 targets, his stereo grid ported to mono. Gives mean angular error and precision. Run it before and after each task, so we also see drift.
- **GazeAOI.cs:** add to any object with a collider to make it an AOI, with a name and a category (tool, material, instruction, safety).
- **GazeLogger.cs:** one CSV row per frame with time, head position and direction, eye gaze direction, confidence, AOI hit by eye gaze, AOI hit by head gaze, and task events. Saved on the Quest.
- Pull logs to the Mac: `adb pull /sdcard/Android/data/com.UnityTechnologies.com.unity.template.urpblank/files/ xr_training/data/sessions/` (install adb with `brew install android-platform-tools`).

Commits:
- `feat(unity): 5x5 accuracy grid for mono tracker`
- `feat(unity): AOI component`
- `feat(unity): per-frame CSV gaze logger`

### Step 6: Training task in TacTool + record sessions (Oct 22 to 23, ~2.5 h): together

Scene 2 already has what we need: a chalkboard with instructions, the blue pillar with a red button, benches with wood, clay, metal and marble, and the hammer, saw and chisel.

Task (2 to 3 minutes): press the red button to start, read the chalkboard, then work through the stations.
New: one small **rule sign** on one bench, e.g. "Marble: chisel only, never the hammer". It sits in plain view but is easy to miss. This is the "noticed or overlooked" probe, and it is tied to behaviour: we log if you fixated the sign AND if you broke the rule.

AOIs (each gets `GazeAOI` + collider):
| AOI | Category |
|---|---|
| Chalkboard | instruction |
| Rule sign | safety |
| Start button | control |
| Wood, clay, metal, marble | material |
| Hammer, saw, chisel | tool |

Events logged: button press (task start), each tool grab and release (from `XRGrabInteractable`), each tool-material hit (from `ToolCollisionDetector.onValidHit`, already there for haptics), rule violation, task end. Materials are already tagged Wood, Metal, Clay, Marble.

Detail: while you hold a tool, it sits right in front of your eyes and would steal every gaze hit. The held tool is excluded from the gaze raycast.

Session protocol (you only, 3 sessions, ideally re-mount between them):
1. Put on headset, check eye image, calibrate (A).
2. Accuracy grid (B).
3. Training task.
4. Accuracy grid again.
5. Pull CSV, commit raw data.

Record the screen of one session with Meta Quest Developer Hub (works on Mac) for the video.

Commits:
- `feat(unity): workstation training task with events`
- `data: pilot sessions 1-3`

### Step 7: Analysis (Oct 24 to 25, ~2.5 h): Claude codes, you run

`xr_training/analysis/metrics.py` produces:
1. **Tracker quality:** accuracy (deg), precision, data loss %, real sample rate, drift (grid before vs after), network delay.
2. **Training metrics per AOI:** time to first fixation, dwell time, fixation count, revisits, and "looked at instruction/safety before first tool grab" (yes/no).
3. **Eye vs head gaze:** the same metrics computed on head gaze. How often they disagree about what you looked at. This is the "when is eye tracking worth it" evidence.
4. **Reliability curve:** using our measured error, how big must an AOI be (in degrees) to be classified correctly 90% of the time. This becomes a concrete rule in the blueprint.

Figures: AOI dwell bars, scanpath timeline, accuracy heatmap, eye vs head comparison, reliability curve.

Commits:
- `feat(analysis): fixation detection and AOI metrics`
- `feat(analysis): eye vs head gaze comparison and reliability curve`
- `results: pilot figures and tables`

### Step 8: Package + apply (Oct 26 to 28, ~2.5 h): together

- **README:** demo GIF at top, one-line pitch, pipeline diagram, results table, how to reproduce, honest limits (N=1, DIY accuracy), credit to Jason Orlosky / JEOresearch, references.
- **docs/BLUEPRINT.md:** one-page draft "when and how to use eye tracking in XR training", based on our numbers. A preview of their thesis deliverable.
- **docs/safety.md:** IR exposure note, self-only use.
- **Demo video (60 to 90 s):** mount -> calibration -> task with gaze ray -> metrics figure.
- Tag release `v1.0`. Pin the repo on your GitHub profile.
- Application: link the repo + video. Claude drafts the paragraph from the results.

Commits:
- `docs: README with results and credits`
- `docs: draft eye tracking blueprint for XR training`
- `docs: IR safety note`

---

## 5. Schedule

| Date | Step | Hours | Gate |
|---|---|---|---|
| Oct 14 | 0 + start 1 | 2 | |
| Oct 15 | 1 + 2 | 2 | **G1** pupil tracked on real eye |
| Oct 16 to 18 | 3 | 3 | **G2** gaze sphere moves in Quest |
| Oct 19 | 4 | 2 | |
| Oct 20 to 21 | 5 | 2.5 | |
| Oct 22 to 23 | 6 | 2.5 | |
| Oct 24 to 25 | 7 | 2.5 | |
| Oct 26 to 28 | 8 + submit | 2.5 | **Submit Oct 28** |
| Oct 29 to 30 | buffer | | deadline |

## 6. Cut lines (if things slip)

- G2 not passed by **Oct 21**: switch the whole pipeline to head gaze. Logger, task, analysis and blueprint stay the same. The tracker becomes a documented bench prototype with its own accuracy numbers. Say so plainly in the README.
- Behind on Oct 25: drop the reliability curve, keep eye vs head comparison.
- Never cut: README, video, honest limits section.
- Out of scope (thesis material, not now): stereo tracking, other participants, live-task comparison, fixing headset slippage.

## 7. Risks

| Risk | What we do |
|---|---|
| Lens reflections break pupil detection | Tilt camera, tape one LED, tune threshold |
| Wi-Fi drops or blocks packets | Phone hotspot; log packet loss via `seq` |
| Keyboard input does nothing on Quest | We read controller buttons through Unity XR input |
| Camera slips during session | Grid before and after measures it; report drift |
| Build loop slow | Test in Editor with mouse gaze first |
| IR safety | Self-only, short sessions, read Orlosky's IR safety page, note in repo |

## 8. Safety

The GC0308 has onboard 850 nm IR LEDs a few cm from the eye inside a closed headset.
Only you wear it until a supervisor has seen the safety note. Sessions under 10 minutes, breaks between.
Reference: https://www.jeoresearch.com/irsafety.html

## 9. References

- Orlosky, J. DIY Eye Tracking for VR. JEO Research. https://www.jeoresearch.com/vrtracker.html
- JEOresearch/EyeTracker (MIT). https://github.com/JEOresearch/EyeTracker
- Volvo Group, MSc Thesis: Eye tracking in Extended Reality (XR) Training (2026).
