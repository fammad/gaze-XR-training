using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// 5x5 accuracy test, head-locked targets. Right controller B (Editor: L) starts it.
/// Each target: settle time, then gaze is recorded; red = settle, yellow = recording.
/// Writes accuracy_yyyyMMdd_HHmmss.csv next to the session log and prints mean error.
/// Accuracy = angle between mean gaze and target. Precision = RMS of sample-to-sample angles.
/// Run it right after calibration and again after the task, to see drift.
/// </summary>
public class AccuracyGrid : MonoBehaviour
{
    public MonoGazeTracker tracker;
    public int columns = 5, rows = 5;
    public float horizontalHalfAngle = 15f;
    public float verticalHalfAngle = 10f;
    public float settleSeconds = 1.0f;
    public float recordSeconds = 1.0f;
    public float targetDistance = 2.0f;
    public float targetRadius = 0.02f;
    public string startKey = "l";

    public bool IsRunning { get; private set; }

    private XRButton startButton;
    private GameObject target;

    private struct Result { public Vector2 targetDeg; public int samples; public float error, precisionRms, sd; public Vector2 meanDeg; }

    private void Awake()
    {
        if (tracker == null) tracker = GetComponent<MonoGazeTracker>();
        if (tracker == null) tracker = FindAnyObjectByType<MonoGazeTracker>();
        startButton = new XRButton(XRNode.LeftHand, CommonUsages.secondaryButton, startKey);   // left Y; the session runs the grid itself
    }

    private void Update()
    {
        if (startButton.Down()) StartGrid();
    }

    /// <summary>Start the grid from code (SessionRunner) or the B button.</summary>
    public void StartGrid()
    {
        if (!IsRunning && tracker != null && !tracker.IsCalibrating) StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        IsRunning = true;
        if (!tracker.IsCalibrated) Debug.LogWarning("[Grid] Running without calibration.");
        GazeLogger.LogEvent("grid_start");
        if (target == null) target = MakeTarget();
        target.SetActive(true);

        var results = new List<Result>();
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                float yaw = columns > 1 ? Mathf.Lerp(-horizontalHalfAngle, horizontalHalfAngle, c / (float)(columns - 1)) : 0f;
                float pitch = rows > 1 ? Mathf.Lerp(verticalHalfAngle, -verticalHalfAngle, r / (float)(rows - 1)) : 0f;
                Vector3 dir = FromYawPitch(yaw, pitch);
                target.transform.localPosition = tracker.eyeOffset + dir * targetDistance;

                SetColor(Color.red);
                yield return new WaitForSeconds(settleSeconds);
                SetColor(Color.yellow);
                var samples = new List<Vector3>();
                float end = Time.time + recordSeconds;
                long lastSeq = -1;
                while (Time.time < end)
                {
                    if (tracker.HasGaze && tracker.Seq != lastSeq)   // one sample per tracker frame
                    {
                        samples.Add(tracker.GazeDirectionLocal);
                        lastSeq = tracker.Seq;
                    }
                    yield return null;
                }
                results.Add(Evaluate(new Vector2(yaw, pitch), dir, samples));
            }
        }
        target.SetActive(false);
        Save(results);
        GazeLogger.LogEvent("grid_end");
        startButton.Buzz(0.8f, 0.3f);
        IsRunning = false;
    }

    private static Result Evaluate(Vector2 targetDeg, Vector3 targetDir, List<Vector3> s)
    {
        var res = new Result { targetDeg = targetDeg, samples = s.Count, error = float.NaN, precisionRms = float.NaN, sd = float.NaN };
        if (s.Count < 3) return res;
        Vector3 mean = Vector3.zero;
        foreach (Vector3 d in s) mean += d;
        mean.Normalize();
        res.error = Vector3.Angle(mean, targetDir);
        float sq = 0f;
        for (int i = 1; i < s.Count; i++) { float a = Vector3.Angle(s[i - 1], s[i]); sq += a * a; }
        res.precisionRms = Mathf.Sqrt(sq / (s.Count - 1));
        float sd = 0f;
        foreach (Vector3 d in s) { float a = Vector3.Angle(d, mean); sd += a * a; }
        res.sd = Mathf.Sqrt(sd / s.Count);
        res.meanDeg = new Vector2(Mathf.Atan2(mean.x, mean.z) * Mathf.Rad2Deg,
                                  Mathf.Atan2(mean.y, new Vector2(mean.x, mean.z).magnitude) * Mathf.Rad2Deg);
        return res;
    }

    private void Save(List<Result> results)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("target_yaw,target_pitch,samples,mean_yaw,mean_pitch,error_deg,precision_rms_deg,sd_deg\n");
        float sum = 0f; int n = 0;
        foreach (Result r in results)
        {
            sb.Append(string.Format(inv, "{0:F1},{1:F1},{2},{3:F2},{4:F2},{5:F2},{6:F2},{7:F2}\n",
                r.targetDeg.x, r.targetDeg.y, r.samples, r.meanDeg.x, r.meanDeg.y, r.error, r.precisionRms, r.sd));
            if (!float.IsNaN(r.error)) { sum += r.error; n++; }
        }
        string dir = Path.Combine(Application.persistentDataPath, "sessions");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "accuracy_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", inv) + ".csv");
        File.WriteAllText(path, sb.ToString());
        string summary = n > 0 ? $"mean error {sum / n:F2} deg over {n}/{results.Count} targets" : "no valid targets";
        Debug.Log($"[Grid] {summary}. Saved {path}");
        GazeLogger.LogEvent("grid_result:" + summary);
    }

    private static Vector3 FromYawPitch(float yawDeg, float pitchDeg)
    {
        float y = yawDeg * Mathf.Deg2Rad, p = pitchDeg * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(p) * Mathf.Sin(y), Mathf.Sin(p), Mathf.Cos(p) * Mathf.Cos(y)).normalized;
    }

    private GameObject MakeTarget()
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "AccuracyTarget";
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(tracker.transform, false);
        go.transform.localScale = Vector3.one * targetRadius * 2f;
        Renderer r = go.GetComponent<Renderer>();
        r.sharedMaterial = GazeMaterials.Make(Color.white);
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go;
    }

    private void SetColor(Color c) => GazeMaterials.SetColor(target.GetComponent<Renderer>().sharedMaterial, c);
}
