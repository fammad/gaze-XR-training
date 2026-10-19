# Add gaze to your Unity scene

Tested with Unity 6000.4, URP, OpenXR + XR Interaction Toolkit 3.x, Meta Quest 3.
The scripts are in `unity/Assets/GazeXR/`. Copy the folder into your project.

## 1. Components on the XR camera

Select the camera that moves with the headset (usually `XR Origin > Camera Offset > Main Camera`) and add:

| Component | Needed | Notes |
|---|---|---|
| `MonoGazeTracker` | yes | receives gaze, calibration, gaze ray. Set `eyeOffset` to the tracked eye: x = -0.032 for the left eye, +0.032 for the right. |
| `GazeLogger` | yes | CSV log in `<persistentDataPath>/sessions/` |
| `AccuracyGrid` | recommended | 25-point accuracy test, writes `accuracy_*.csv` |
| `GazeHeatmap` | optional | live heatmap on surfaces |
| `GazeScanpath` | optional | fixation spheres and lines; also owns the view toggle |
| `SessionRunner` | optional | runs calibration, grid, task, grid from one button |

`GazeUdpReceiver` is added at runtime. Gaze packets arrive on UDP port 5005.

## 2. Areas of interest

Add `GazeAOI` to every object you want in the results and pick a category
(Instruction, Safety, Control, Material, Tool, Other). The object needs a collider.
The log then has `eye_aoi` and `head_aoi` for every frame.

## 3. Task events (optional)

`TrainingTask` logs grabs, releases and tool hits from XR Interaction Toolkit grab interactables.
Add it to any GameObject. For your own events, call `GazeLogger.LogEvent("something")` from any script;
the text is written into the `events` column of the next row.

## 4. Android / Quest settings

- Player Settings > Android > Internet Access: **Require** (UDP needs it).
- Graphics settings: `Universal Render Pipeline/Unlit` must be in Always Included Shaders
  (already set in this project; copy it if you use your own).
- Build And Run with the headset on USB and developer mode on.

## 5. Controls

| Right controller | Editor key | Action |
|---|---|---|
| A | P | run the session (calibration, grid, task, grid). During the task: end it. |
| B | K | calibration only |
| Thumbstick click | H | view: heatmap, scanpath, both, off |
| | L | accuracy grid only |

In the Editor, with no tracker running, the mouse works as fake gaze, so you can test everything without a headset.

## 6. Session output

`session_*.csv` columns: `time, frame, head position and forward, gaze_source, calibrated, seq, confidence,
eye ray origin and direction, eye_aoi, eye_aoi_category, eye_hit_distance, head_aoi, head_aoi_category,
head_hit_distance, events`.

Events include `calibrated:<model>:n=..:mean=..:worst=..`, `grid_result:...`, `phase:...`,
`grab:<tool>`, `hit:<tool>:<material>` and `fix:<object>:<duration ms>`.

Pull the logs with:
```bash
adb pull /sdcard/Android/data/<your.package.name>/files/sessions/ sessions/
```
(`tools/run_session.sh` does this for you; change the package name in it if yours is different.)
