using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Mono (left eye) gaze from the DIY IR camera, based on Jason Orlosky's EyeTracker.cs (MIT).
/// Changes: gaze comes over UDP (GazeUdpReceiver) instead of a text file; controller buttons
/// instead of the keyboard; automatic 9-dot calibration with a 2nd-order polynomial map and an
/// instant accuracy readout; gaze origin at the left eye instead of head centre.
///
/// Put this on the XR camera (Main Camera under XR Origin).
/// Right controller B (Editor: K) = calibrate only (A runs the whole session, see SessionRunner): one press runs all 9 dots by itself (keep the head
/// still, follow the dots with the eyes). Press A again during the run to cancel.
/// In the Editor with no tracker running, the mouse acts as fake gaze.
/// </summary>
[DefaultExecutionOrder(-10)]
public class MonoGazeTracker : MonoBehaviour
{
    public enum GazeSource { None, Tracker, Mouse }

    [Header("Input")]
    public GazeUdpReceiver receiver;
    [Tooltip("Packets older than this count as no gaze (seconds).")]
    public float maxSampleAge = 0.25f;
    [Tooltip("Tracker confidence below this counts as no gaze.")]
    public float minConfidence = 0.5f;
    public bool flipX = false;
    public bool flipY = false;

    [Header("Eye")]
    [Tooltip("Gaze origin relative to the camera. Left eye: about -0.032 m on x.")]
    public Vector3 eyeOffset = new Vector3(-0.032f, 0f, 0f);

    [Header("Visualization")]
    public bool showGazeSphere = true;
    public float sphereDistance = 2.0f;
    public float sphereRadius = 0.02f;
    [Range(0f, 0.95f)] public float displaySmoothing = 0.5f;

    [Header("Calibration (9 dots, automatic)")]
    public float calibrationYawDegrees = 12f;
    public float calibrationPitchDegrees = 9f;
    [Tooltip("Time for the eye to land on a new dot before samples count.")]
    public float settleSeconds = 0.9f;
    [Tooltip("Samples are collected for this long per dot; the median is used.")]
    public float captureSeconds = 0.8f;
    public int minSamplesPerTarget = 8;
    [Tooltip("How long the result (dots + error) stays visible.")]
    public float resultSeconds = 6f;
    public string calibrateKey = "k";

    [Header("Editor")]
    public bool useMouseWhenNoTracker = true;

    // ---- public state, read by GazeLogger and AccuracyGrid ----
    public GazeSource Source { get; private set; }
    public bool HasGaze => Source != GazeSource.None;
    /// <summary>Calibrated gaze direction in camera space (not smoothed).</summary>
    public Vector3 GazeDirectionLocal { get; private set; } = Vector3.forward;
    /// <summary>Calibrated gaze ray in world space (not smoothed).</summary>
    public Ray GazeRayWorld { get; private set; }
    public float Confidence { get; private set; }
    public long Seq { get; private set; } = -1;
    public bool IsCalibrated { get; private set; }
    public bool IsCalibrating => step >= 0;
    /// <summary>Mean error over the calibration dots after the last calibration (degrees).</summary>
    public float LastCalibrationError { get; private set; } = -1f;

    // ---- calibration model (see MapRaw) ----
    private float[] coefYaw = { 0f, 1f, 0f }, coefPitch = { 0f, 0f, 1f };

    private static readonly Vector2[] Targets =   // (yaw, pitch) in units of the calibration angles
    {
        new Vector2(0, 0), new Vector2(-1, 1), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0),
        new Vector2(1, -1), new Vector2(0, -1), new Vector2(-1, -1), new Vector2(-1, 0)
    };
    private int step = -1;               // -1 = idle, otherwise index of the target on screen
    private bool capturing;
    private long lastCapturedSeq = -1;
    private Coroutine calibration;
    private readonly List<Vector2> captureBuffer = new List<Vector2>();
    private readonly List<Vector2> calibRaw = new List<Vector2>();
    private readonly List<Vector2> calibTargets = new List<Vector2>();
    private Transform resultsRoot;
    private TMPro.TextMeshPro hud;

    private Vector3 rawDirectionLocal = Vector3.forward;   // uncalibrated, after flips
    private Vector3 displayDirection = Vector3.forward;
    private GameObject gazeSphere, targetSphere;
    private XRButton calibrateButton;
    private Camera cam;

    private void Awake()
    {
        if (receiver == null) receiver = FindAnyObjectByType<GazeUdpReceiver>();
        if (receiver == null) receiver = gameObject.AddComponent<GazeUdpReceiver>();
        cam = GetComponent<Camera>();
        calibrateButton = new XRButton(XRNode.RightHand, UnityEngine.XR.CommonUsages.secondaryButton, calibrateKey);
        gazeSphere = MakeSphere("GazeSphere", Color.cyan);
    }

    private void Update()
    {
        ReadRawGaze();
        if (HasGaze)
        {
            GazeDirectionLocal = ApplyCalibration(rawDirectionLocal);
            Vector3 origin = transform.TransformPoint(eyeOffset);
            GazeRayWorld = new Ray(origin, transform.TransformDirection(GazeDirectionLocal));
            if (capturing && (Source != GazeSource.Tracker || Seq != lastCapturedSeq))
            {
                captureBuffer.Add(ToYawPitch(rawDirectionLocal));   // one sample per tracker frame
                lastCapturedSeq = Seq;
            }
            displayDirection = Vector3.Slerp(GazeDirectionLocal, displayDirection, displaySmoothing).normalized;
        }

        gazeSphere.SetActive(showGazeSphere && HasGaze && !IsCalibrating);
        gazeSphere.transform.localPosition = eyeOffset + displayDirection * sphereDistance;

        if (calibrateButton.Down()) OnCalibratePressed();
    }

    // ------------------------------------------------------------------ gaze input
    private void ReadRawGaze()
    {
        Source = GazeSource.None;
        Confidence = 0f;

        bool trackerAlive = receiver != null && receiver.SecondsSinceLastPacket < 1.0;
        if (trackerAlive && receiver.TryGetLatest(out GazeUdpReceiver.Sample s)
            && receiver.Now - s.receiveTime <= maxSampleAge
            && s.confidence >= minConfidence && s.direction.sqrMagnitude > 1e-6f)
        {
            Vector3 d = s.direction.normalized;
            if (flipX) d.x = -d.x;
            if (flipY) d.y = -d.y;
            rawDirectionLocal = d;
            Confidence = s.confidence;
            Seq = s.seq;
            Source = GazeSource.Tracker;
            return;
        }

        if (!trackerAlive && useMouseWhenNoTracker && Application.isEditor && cam != null
            && TryGetMousePosition(out Vector2 mouse))
        {
            Ray r = cam.ScreenPointToRay(mouse);
            rawDirectionLocal = transform.InverseTransformDirection(r.direction).normalized;
            Confidence = 1f;
            Source = GazeSource.Mouse;
        }
    }

    private static bool TryGetMousePosition(out Vector2 position)
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null) { position = Mouse.current.position.ReadValue(); return true; }
        position = default;
        return false;
#else
        position = Input.mousePosition;
        return true;
#endif
    }

    // ------------------------------------------------------------------ calibration model
    // Raw gaze as (x, y) = yaw/pitch of the raw direction in degrees. Calibrated yaw and pitch are
    // each a 2nd-order polynomial of (x, y): c0 + c1 x + c2 y + c3 xy + c4 x^2 + c5 y^2.
    // With fewer than 8 good targets only the affine part (c0..c2) is fitted.
    private static float[] Terms(Vector2 r, int n)
    {
        float[] t = { 1f, r.x, r.y, r.x * r.y, r.x * r.x, r.y * r.y };
        if (n == 6) return t;
        return new[] { t[0], t[1], t[2] };
    }

    private static float Eval(float[] c, Vector2 r)
    {
        float[] t = Terms(r, c.Length);
        float s = 0f;
        for (int i = 0; i < c.Length; i++) s += c[i] * t[i];
        return s;
    }

    private Vector2 MapRaw(Vector2 raw) => new Vector2(Eval(coefYaw, raw), Eval(coefPitch, raw));

    private Vector3 ApplyCalibration(Vector3 raw)
    {
        if (!IsCalibrated) return raw;
        return FromYawPitch(MapRaw(ToYawPitch(raw)));
    }

    private static Vector2 ToYawPitch(Vector3 d)
    {
        d.Normalize();
        float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        float pitch = Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
        return new Vector2(yaw, pitch);
    }

    private static Vector3 FromYawPitch(Vector2 yp)
    {
        float y = yp.x * Mathf.Deg2Rad, p = yp.y * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(p) * Mathf.Sin(y), Mathf.Sin(p), Mathf.Cos(p) * Mathf.Cos(y)).normalized;
    }

    /// <summary>Least squares with a tiny ridge term. Returns null if the system is singular.</summary>
    private static float[] Fit(List<Vector2> raw, List<float> target, int n)
    {
        double[,] A = new double[n, n + 1];
        for (int k = 0; k < raw.Count; k++)
        {
            float[] t = Terms(raw[k], n);
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) A[i, j] += t[i] * t[j];
                A[i, n] += t[i] * target[k];
            }
        }
        for (int i = 0; i < n; i++) A[i, i] += 1e-6;
        for (int col = 0; col < n; col++)   // Gaussian elimination, partial pivoting
        {
            int piv = col;
            for (int r = col + 1; r < n; r++) if (System.Math.Abs(A[r, col]) > System.Math.Abs(A[piv, col])) piv = r;
            if (System.Math.Abs(A[piv, col]) < 1e-9) return null;
            for (int j = 0; j <= n; j++) { double tmp = A[col, j]; A[col, j] = A[piv, j]; A[piv, j] = tmp; }
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double f = A[r, col] / A[col, col];
                for (int j = col; j <= n; j++) A[r, j] -= f * A[col, j];
            }
        }
        float[] c = new float[n];
        for (int i = 0; i < n; i++) c[i] = (float)(A[i, n] / A[i, i]);
        return c;
    }

    // ------------------------------------------------------------------ calibration procedure
    /// <summary>Start the 9-dot calibration from code (SessionRunner). Does nothing if one is running.</summary>
    public void StartCalibration()
    {
        if (calibration == null) calibration = StartCoroutine(RunCalibration());
    }

    private void OnCalibratePressed()
    {
        if (calibration != null)            // second press cancels
        {
            StopCoroutine(calibration);
            calibration = null;
            EndCalibrationVisuals();
            Debug.Log("[Gaze] Calibration cancelled.");
            return;
        }
        calibration = StartCoroutine(RunCalibration());
    }

    private IEnumerator RunCalibration()
    {
        step = 0;
        HideResults();
        calibRaw.Clear(); calibTargets.Clear();
        GazeLogger.LogEvent("calibration_start");
        ShowHud("Keep your head still.\nLook at each dot until it disappears.", 0f);
        yield return new WaitForSeconds(1.5f);
        ShowHud("", 0f);

        for (int i = 0; i < Targets.Length; i++)
        {
            step = i;
            Vector2 target = Vector2.Scale(Targets[i], new Vector2(calibrationYawDegrees, calibrationPitchDegrees));
            bool ok = false;
            for (int attempt = 0; attempt < 2 && !ok; attempt++)
            {
                ShowTarget(target);
                // settle: the dot shrinks, which pulls the eye to its centre
                for (float t = 0; t < settleSeconds; t += Time.deltaTime)
                {
                    SetTargetScale(Mathf.Lerp(3f, 1f, t / settleSeconds));
                    yield return null;
                }
                SetTargetScale(1f);
                SetTargetColor(Color.yellow);
                captureBuffer.Clear();
                capturing = true;
                yield return new WaitForSeconds(captureSeconds);
                capturing = false;
                if (captureBuffer.Count >= minSamplesPerTarget)
                {
                    calibRaw.Add(Median(captureBuffer));
                    calibTargets.Add(target);
                    ok = true;
                }
                else
                {
                    calibrateButton.Buzz(1f, 0.2f);   // lost the pupil: try this dot once more
                }
            }
            calibrateButton.Buzz(0.3f, 0.04f);
        }
        HideTarget();
        FinishCalibration();
        step = -1;
        calibration = null;
    }

    private static Vector2 Median(List<Vector2> v)
    {
        List<float> xs = new List<float>(), ys = new List<float>();
        foreach (Vector2 p in v) { xs.Add(p.x); ys.Add(p.y); }
        xs.Sort(); ys.Sort();
        return new Vector2(xs[xs.Count / 2], ys[ys.Count / 2]);
    }

    private void FinishCalibration()
    {
        int n = calibRaw.Count >= 8 ? 6 : 3;
        if (calibRaw.Count < 4)
        {
            ShowHud($"Calibration failed: only {calibRaw.Count} of 9 dots had a tracked pupil.\nCheck the tracker window, then press A again.", 6f);
            calibrateButton.Buzz(1f, 0.5f);
            GazeLogger.LogEvent($"calibration_failed:{calibRaw.Count}");
            return;
        }
        List<float> ty = new List<float>(), tp = new List<float>();
        foreach (Vector2 t in calibTargets) { ty.Add(t.x); tp.Add(t.y); }
        float[] cy = Fit(calibRaw, ty, n), cp = Fit(calibRaw, tp, n);
        if (cy == null || cp == null)
        {
            ShowHud("Calibration failed: the dots gave almost the same eye position.\nIs the pupil tracked? Press A again.", 6f);
            calibrateButton.Buzz(1f, 0.5f);
            GazeLogger.LogEvent("calibration_failed:singular");
            return;
        }
        coefYaw = cy; coefPitch = cp;
        IsCalibrated = true;
        GazeHeatmap heatmap = FindAnyObjectByType<GazeHeatmap>();
        if (heatmap != null) heatmap.Clear();   // dots from before this calibration are wrong
        GazeScanpath scanpath = FindAnyObjectByType<GazeScanpath>();
        if (scanpath != null) scanpath.Clear();

        // validation on the same dots: where would the calibrated gaze have landed?
        float sum = 0f, worst = 0f;
        ShowResultsStart();
        for (int i = 0; i < calibRaw.Count; i++)
        {
            Vector3 predicted = FromYawPitch(MapRaw(calibRaw[i]));
            Vector3 truth = FromYawPitch(calibTargets[i]);
            float err = Vector3.Angle(predicted, truth);
            sum += err; worst = Mathf.Max(worst, err);
            AddResultMarker(truth, predicted, err);
        }
        LastCalibrationError = sum / calibRaw.Count;
        string model = n == 6 ? "polynomial" : "linear";
        ShowHud($"Calibrated ({model}, {calibRaw.Count}/9 dots)\nmean error {LastCalibrationError:F1}°, worst {worst:F1}°\nB = redo", resultSeconds);
        Invoke(nameof(HideResults), resultSeconds);
        calibrateButton.Buzz();
        Debug.Log($"[Gaze] Calibrated {model}, {calibRaw.Count} dots, mean {LastCalibrationError:F2} deg, worst {worst:F2} deg");
        GazeLogger.LogEvent($"calibrated:{model}:n={calibRaw.Count}:mean={LastCalibrationError:F2}:worst={worst:F2}");
    }

    // ------------------------------------------------------------------ visuals
    private GameObject MakeSphere(string name, Color color, Transform parent = null)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        Destroy(go.GetComponent<Collider>());   // must not block gaze raycasts
        go.transform.SetParent(parent != null ? parent : transform, false);
        go.transform.localScale = Vector3.one * sphereRadius * 2f;
        Renderer r = go.GetComponent<Renderer>();
        r.sharedMaterial = GazeMaterials.Make(color);
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go;
    }

    private void ShowTarget(Vector2 yawPitch)
    {
        if (targetSphere == null) targetSphere = MakeSphere("CalibrationTarget", Color.red);
        targetSphere.SetActive(true);
        SetTargetColor(Color.red);
        targetSphere.transform.localPosition = eyeOffset + FromYawPitch(yawPitch) * sphereDistance;
    }

    private void SetTargetScale(float k)
    {
        if (targetSphere != null) targetSphere.transform.localScale = Vector3.one * sphereRadius * 2f * k;
    }

    private void SetTargetColor(Color c)
    {
        if (targetSphere != null) GazeMaterials.SetColor(targetSphere.GetComponent<Renderer>().sharedMaterial, c);
    }

    private void HideTarget()
    {
        if (targetSphere != null) targetSphere.SetActive(false);
    }

    private void EndCalibrationVisuals()
    {
        HideTarget();
        capturing = false;
        step = -1;
        ShowHud("", 0f);
    }

    private void ShowResultsStart()
    {
        CancelInvoke(nameof(HideResults));
        HideResults();
        resultsRoot = new GameObject("CalibrationResult").transform;
        resultsRoot.SetParent(transform, false);
    }

    private void AddResultMarker(Vector3 truth, Vector3 predicted, float err)
    {
        GameObject t = MakeSphere("target", Color.white, resultsRoot);
        t.transform.localPosition = eyeOffset + truth * sphereDistance;
        t.transform.localScale = Vector3.one * sphereRadius * 1.2f;
        Color c = err < 1.5f ? Color.green : err < 3f ? Color.yellow : Color.red;
        GameObject p = MakeSphere("gaze", c, resultsRoot);
        p.transform.localPosition = eyeOffset + predicted * sphereDistance;
        p.transform.localScale = Vector3.one * sphereRadius * 1.6f;
    }

    private void HideResults()
    {
        if (resultsRoot != null) Destroy(resultsRoot.gameObject);
        resultsRoot = null;
    }

    public void ShowHud(string text, float seconds)
    {
        if (hud == null)
        {
            GameObject go = new GameObject("GazeHud");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, -0.35f, 1.5f);
            hud = go.AddComponent<TMPro.TextMeshPro>();
            hud.fontSize = 0.6f;
            hud.alignment = TMPro.TextAlignmentOptions.Center;
            hud.rectTransform.sizeDelta = new Vector2(1.6f, 0.5f);
            hud.color = Color.white;
        }
        hud.text = text;
        hud.gameObject.SetActive(!string.IsNullOrEmpty(text));
        CancelInvoke(nameof(ClearHud));
        if (seconds > 0f) Invoke(nameof(ClearHud), seconds);
    }

    private void ClearHud() => ShowHud("", 0f);
}
