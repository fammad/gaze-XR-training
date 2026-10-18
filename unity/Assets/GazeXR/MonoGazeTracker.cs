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
/// instead of the keyboard; 5-point calibration (centre, up, right, down, left) instead of 3-point
/// vertical; gaze origin at the left eye instead of head centre.
///
/// Put this on the XR camera (Main Camera under XR Origin).
/// Right controller A (Editor: K) = calibrate: first press shows the centre target, then each press
/// captures the target you are looking at and shows the next one.
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

    [Header("Calibration")]
    public float calibrationAngleDegrees = 10f;
    [Tooltip("Gaze is averaged over this long when a target is captured.")]
    public float captureSeconds = 0.8f;
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

    // ---- calibration model: rotation to centre, then a 2x2 linear map on (yaw, pitch) ----
    private Quaternion centerRotation = Quaternion.identity;
    private float m00 = 1f, m01 = 0f, m10 = 0f, m11 = 1f;

    private static readonly Vector2[] Targets =   // (yaw, pitch) in units of calibrationAngleDegrees
    {
        new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 0), new Vector2(0, -1), new Vector2(-1, 0)
    };
    private int step = -1;               // -1 = idle, otherwise index of the target on screen
    private bool capturing;
    private readonly List<Vector3> captureBuffer = new List<Vector3>();
    private readonly List<Vector3> observedCentered = new List<Vector3>();
    private Vector3 rawCenterMean;

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
        calibrateButton = new XRButton(XRNode.RightHand, UnityEngine.XR.CommonUsages.primaryButton, calibrateKey);
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
            if (capturing) captureBuffer.Add(rawDirectionLocal);
            displayDirection = Vector3.Slerp(GazeDirectionLocal, displayDirection, displaySmoothing).normalized;
        }

        gazeSphere.SetActive(showGazeSphere && HasGaze);
        gazeSphere.transform.localPosition = eyeOffset + displayDirection * sphereDistance;

        if (calibrateButton.Down() && !capturing) OnCalibratePressed();
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
    private Vector3 ApplyCalibration(Vector3 raw)
    {
        if (!IsCalibrated) return raw;
        Vector2 a = ToYawPitch(centerRotation * raw);
        Vector2 corrected = new Vector2(m00 * a.x + m01 * a.y, m10 * a.x + m11 * a.y);
        return FromYawPitch(corrected);
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

    // ------------------------------------------------------------------ calibration procedure
    private void OnCalibratePressed()
    {
        if (step < 0)
        {
            step = 0;
            observedCentered.Clear();
            ShowTarget(step);
            Debug.Log("[Gaze] Calibration: look at the red dot and press A. 5 targets.");
            return;
        }
        StartCoroutine(CaptureTarget());
    }

    private IEnumerator CaptureTarget()
    {
        capturing = true;
        captureBuffer.Clear();
        SetTargetColor(Color.yellow);
        yield return new WaitForSeconds(captureSeconds);
        capturing = false;

        if (captureBuffer.Count < 5)
        {
            Debug.LogWarning($"[Gaze] Only {captureBuffer.Count} gaze samples, try this target again.");
            SetTargetColor(Color.red);
            calibrateButton.Buzz(1f, 0.3f);
            yield break;
        }
        Vector3 mean = Vector3.zero;
        foreach (Vector3 d in captureBuffer) mean += d;
        mean.Normalize();
        calibrateButton.Buzz();

        if (step == 0)
        {
            rawCenterMean = mean;
        }
        else
        {
            observedCentered.Add(mean);   // stored raw; rotated once the centre is known
        }

        step++;
        if (step < Targets.Length)
        {
            ShowTarget(step);
            yield break;
        }
        FinishCalibration();
    }

    private void FinishCalibration()
    {
        Quaternion rot = Quaternion.FromToRotation(rawCenterMean, Vector3.forward);
        // least squares: target = M * observed, over the 4 outer targets
        float sto00 = 0, sto01 = 0, sto10 = 0, sto11 = 0, soo00 = 0, soo01 = 0, soo11 = 0;
        for (int i = 0; i < observedCentered.Count; i++)
        {
            Vector2 o = ToYawPitch(rot * observedCentered[i]);
            Vector2 t = Targets[i + 1] * calibrationAngleDegrees;
            sto00 += t.x * o.x; sto01 += t.x * o.y; sto10 += t.y * o.x; sto11 += t.y * o.y;
            soo00 += o.x * o.x; soo01 += o.x * o.y; soo11 += o.y * o.y;
        }
        float det = soo00 * soo11 - soo01 * soo01;
        if (Mathf.Abs(det) < 1e-3f)
        {
            Debug.LogWarning("[Gaze] Calibration failed: the targets gave almost the same gaze. Is the pupil tracked?");
            HideTarget();
            step = -1;
            calibrateButton.Buzz(1f, 0.5f);
            return;
        }
        float i00 = soo11 / det, i01 = -soo01 / det, i11 = soo00 / det;
        centerRotation = rot;
        m00 = sto00 * i00 + sto01 * i01;
        m01 = sto00 * i01 + sto01 * i11;
        m10 = sto10 * i00 + sto11 * i01;
        m11 = sto10 * i01 + sto11 * i11;
        IsCalibrated = true;
        HideTarget();
        step = -1;
        Debug.Log($"[Gaze] Calibrated. Map yaw' = {m00:F2} yaw + {m01:F2} pitch, pitch' = {m10:F2} yaw + {m11:F2} pitch");
        GazeLogger.LogEvent("calibrated");
    }

    // ------------------------------------------------------------------ visuals
    private GameObject MakeSphere(string name, Color color)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        Destroy(go.GetComponent<Collider>());   // must not block gaze raycasts
        go.transform.SetParent(transform, false);
        go.transform.localScale = Vector3.one * sphereRadius * 2f;
        Renderer r = go.GetComponent<Renderer>();
        r.material.color = color;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go;
    }

    private void ShowTarget(int index)
    {
        if (targetSphere == null) targetSphere = MakeSphere("CalibrationTarget", Color.red);
        targetSphere.SetActive(true);
        SetTargetColor(Color.red);
        targetSphere.transform.localPosition = eyeOffset + FromYawPitch(Targets[index] * calibrationAngleDegrees) * sphereDistance;
    }

    private void SetTargetColor(Color c)
    {
        if (targetSphere != null) targetSphere.GetComponent<Renderer>().material.color = c;
    }

    private void HideTarget()
    {
        if (targetSphere != null) targetSphere.SetActive(false);
    }
}
