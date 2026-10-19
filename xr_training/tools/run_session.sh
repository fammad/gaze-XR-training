#!/bin/bash
# One command for a full recorded session.
#   xr_training/tools/run_session.sh [QUEST_IP]
# Starts the eye tracker (records eye video + gaze), records the headset view with scrcpy over USB,
# and when you press Enter (or Ctrl+C): stops both cleanly and pulls the headset logs into the same folder.
# Needs: brew install scrcpy ; headset connected by USB with developer mode on.
set -u
cd "$(dirname "$0")/../.."
QUEST_IP="${1:-192.168.0.106}"
OUT="sessions/$(date +%Y%m%d_%H%M%S)"
mkdir -p "$OUT"
source .venv/bin/activate 2>/dev/null
# a tracker left over from an earlier run would fight for the camera and send duplicate packets
pkill -f Orlosky3DEyeTracker.py 2>/dev/null && sleep 1

python -u 3DTracker/Orlosky3DEyeTracker.py --camera 0 --udp-host "$QUEST_IP" --record "$OUT" > "$OUT/tracker.log" 2>&1 &
TRACKER=$!
SCRCPY=""
if command -v scrcpy >/dev/null; then
  # Quest 3 shows both eyes side by side; crop the left eye later with
  #   ffmpeg -i headset.mp4 -vf "crop=iw/2:ih:0:0" headset_left.mp4
  scrcpy --no-audio --no-playback --max-size 1920 --video-bit-rate 8M \
         --record "$OUT/headset.mp4" > "$OUT/scrcpy.log" 2>&1 &
  SCRCPY=$!
else
  echo "scrcpy not found (brew install scrcpy): headset view will not be recorded."
fi
date +%s.%N > "$OUT/start_time.txt"

finish() {
  trap - INT
  echo "Stopping, saving videos..."
  [ -n "$SCRCPY" ] && kill -TERM "$SCRCPY" 2>/dev/null
  kill -TERM "$TRACKER" 2>/dev/null
  for i in $(seq 1 20); do   # up to 10 s for both to finish writing
    kill -0 "$TRACKER" 2>/dev/null || { [ -n "$SCRCPY" ] && kill -0 "$SCRCPY" 2>/dev/null; } || break
    sleep 0.5
  done
  mkdir -p "$OUT/headset_logs"
  if adb pull /sdcard/Android/data/com.UnityTechnologies.com.unity.template.urpblank/files/sessions/. "$OUT/headset_logs" > "$OUT/adb.log" 2>&1; then
    echo "Pulled headset logs."
  else
    echo "Could not pull headset logs, see $OUT/adb.log"
  fi
  echo "Done: $OUT"
  ls -la "$OUT"
  exit 0
}
trap finish INT

echo "Recording into $OUT"
echo "Put the headset on, press A to run the session. Press Enter here when done."
read -r
finish
