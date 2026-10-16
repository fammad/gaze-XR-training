# Unity setup for gaze (Steps 3, 5, 6)

Scripts are in `unity/Assets/GazeXR/`. Nothing here was compiled yet; check the Console first.

## 1. Add the components (once)
1. Open `unity` in Unity Hub (6000.4.5f1), open `Assets/Scenes/Playground.unity`. Console must show no red errors.
2. Hierarchy: `XR Origin (VR) > Camera Offset > Main Camera`. Add Component: `MonoGazeTracker`, `GazeLogger`, `AccuracyGrid`, `GazeHeatmap`. (`GazeUdpReceiver` is added by itself at Play.)
3. Create an empty GameObject `TrainingTask`, add `TrainingTask`.
4. AOIs: add `GazeAOI` and pick the category. Each needs a collider (add a Box Collider if missing).

| Object | Category |
|---|---|
| Chalkboard | Instruction |
| Rule sign (new, below) | Safety |
| Red start button | Control |
| Wood, Metal, Clay, Marble blocks | Material |
| Hammer, Saw, Chisel | Tool |

5. Rule sign: on the marble bench, GameObject > 3D Object > Quad (about 0.3 x 0.15 m), child GameObject > 3D Object > Text - TextMeshPro: "Marble: chisel only. Never the hammer." Small and plain, it should be easy to miss. Add `GazeAOI` (Safety) to the Quad.
6. Optional: on the red button's interaction event, add `TrainingTask.StartTask`.
7. Save the scene. Commit the new `.meta` files and the scene in GitHub Desktop.

## 2. Controls
| Action | Headset | Editor |
|---|---|---|
| Calibrate (press once to start, then once per red dot, 5 dots) | right A | K |
| Accuracy grid, 25 dots, about 50 s | right B | L |
| Task start / end | left X | O |
| Heatmap show / hide | right thumbstick click | H |

Calibration dot: red = look at it and press, yellow = recording (keep looking 0.8 s), then the next dot. Long buzz = not enough gaze, press again.

## 3. Test in the Editor (no headset, no tracker)
Play. With no tracker running, the mouse is fake gaze (cyan dot). Press K six times while pointing the mouse at each red dot, then L, then O. Console should show `[GazeLog] Logging to ...` and `[Grid] mean error ...`.
If the XR Interaction Simulator uses one of these keys, change `calibrateKey` / `startKey` / `toggleKey` in the Inspector.

## 4. On the Quest
1. Mac and Quest on the same network (home Wi-Fi or phone hotspot, not eduroam). Quest IP: Settings > Wi-Fi > your network.
2. Unity: Build Profiles > Meta Quest > Build And Run (headset on USB-C, developer mode).
3. Mac: `python 3DTracker/Orlosky3DEyeTracker.py --camera 1 --udp-host <QUEST_IP>`
4. In the headset: look around a few seconds, then A to calibrate, B for the grid, X to start the task, X to end, B again for the grid.
5. Pull logs: `adb pull /sdcard/Android/data/com.UnityTechnologies.com.unity.template.urpblank/files/sessions/ xr_training/data/sessions/`
