#!/bin/bash
# One command for a full recorded session.
#   xr_training/tools/run_session.sh [QUEST_IP]
# Starts the eye tracker (records eye video + gaze), records the headset view with scrcpy over USB,
# and when you press Enter: stops both and pulls the headset logs into the same folder.
# Needs: brew install scrcpy ; headset connected by USB with developer mode on.
set -u
cd "$(dirname "$0")/../.."
QUEST_IP="${1:-192.168.0.106}"
OUT="sessions/$(date +%Y%m%d_%H%M%S)"
mkdir -p "$OUT"
source .venv/bin/activate 2>/dev/null

python 3DTracker/Orlosky3DEyeTracker.py --camera 0 --udp-host "$QUEST_IP" --record "$OUT" > "$OUT/tracker.log" 2>&1 &
TRACKER=$!

if command -v scrcpy >/dev/null; then
  # Quest 3 shows both eyes side by side; crop the left eye later with
  #   ffmpeg -i headset.mp4 -vf "crop=iw/2:ih:0:0" headset_left.mp4
  scrcpy --no-audio --no-playback --max-size 1920 --record "$OUT/headset.mp4" > "$OUT/scrcpy.log" 2>&1 &
  SCRCPY=$!
else
  echo "scrcpy not found (brew install scrcpy): headset view will not be recorded."
  SCRCPY=""
fi

date +%s.%N > "$OUT/start_time.txt"
echo "Recording into $OUT"
echo "Put the headset on, press Y to run the session. Press Enter here when done."
read -r

[ -n "$SCRCPY" ] && kill -INT "$SCRCPY"
kill -INT "$TRACKER"
wait 2>/dev/null
adb pull /sdcard/Android/data/com.UnityTechnologies.com.unity.template.urpblank/files/sessions/ "$OUT/headset_logs" > /dev/null \
  && echo "Pulled headset logs." || echo "Could not pull headset logs (is the USB cable in?)."
echo "Done: $OUT"
ls -la "$OUT"
