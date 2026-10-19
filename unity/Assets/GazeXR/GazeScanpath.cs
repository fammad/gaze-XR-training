using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Live scanpath, in the style of classic eye-tracking replays: every fixation becomes a sphere on the
/// surface you looked at (bigger and redder the longer you looked), and consecutive fixations are
/// joined by a line (the saccade). Fast eye jumps leave no trace, unlike the raw heatmap.
///
/// Fixation detection is I-VT (velocity threshold) on the world gaze direction, one sample per
/// tracker packet: below velocityThreshold deg/s = fixating. That also keeps a fixation when you
/// turn your head but keep looking at the same thing.
///
/// Right thumbstick click (Editor: H) cycles the view: heatmap -> scanpath -> both -> off.
/// Each fixation is also written to the gaze log: fix:<object>:<duration ms>.
/// Put it on the XR camera next to MonoGazeTracker.
/// </summary>
[DefaultExecutionOrder(55)]
public class GazeScanpath : MonoBehaviour
{
    public MonoGazeTracker tracker;
    public GazeHeatmap heatmap;

    [Header("Fixation detection (I-VT)")]
    [Tooltip("Gaze slower than this (deg/s) counts as fixating.")]
    public float velocityThreshold = 40f;
    [Tooltip("Shorter fixations are dropped.")]
    public float minFixationSeconds = 0.12f;
    [Tooltip("A gap in tracking longer than this ends the fixation.")]
    public float maxGapSeconds = 0.3f;
    public float maxRayDistance = 20f;

    [Header("Look")]
    public float baseRadius = 0.012f;
    [Tooltip("Radius grows with sqrt(duration) times this.")]
    public float growPerSqrtSecond = 0.025f;
    public float lineWidth = 0.003f;
    public Color lineColor = new Color(1f, 1f, 1f, 0.8f);
    public int maxFixations = 400;
    public string toggleKey = "h";

    public static bool IsFixating { get; private set; }

    public enum View { Heatmap, Scanpath, Both, Off }
    public View view = View.Both;

    private class Fixation { public GameObject sphere; public Vector3 point; }
    private readonly List<Fixation> fixations = new List<Fixation>();
    private Transform root;
    private LineRenderer path;
    private XRButton toggle;
    private AccuracyGrid grid;

    // current fixation
    private bool inFix;
    private float fixStart, lastSampleTime;
    private Vector3 fixSum; private int fixCount;
    private string fixObject;
    private Vector3 lastDir; private long lastSeq = -1; private bool haveLast;
    private int fastStreak;
    private Fixation live;   // sphere growing while you look

    private static readonly Color[] Ramp =
    {
        new Color(0.2f, 0.4f, 1f), new Color(0.1f, 0.9f, 1f), new Color(0.2f, 1f, 0.4f),
        new Color(1f, 1f, 0.2f), new Color(1f, 0.55f, 0.1f), new Color(1f, 0.15f, 0.1f)
    };
    private static readonly float[] RampSeconds = { 0f, 0.2f, 0.4f, 0.8f, 1.5f, 3f };

    private void Awake()
    {
        if (tracker == null) tracker = GetComponent<MonoGazeTracker>();
        if (heatmap == null) heatmap = GetComponent<GazeHeatmap>();
        if (heatmap != null) heatmap.externalToggle = true;
        grid = FindAnyObjectByType<AccuracyGrid>();
        toggle = new XRButton(XRNode.RightHand, CommonUsages.primary2DAxisClick, toggleKey);
        root = new GameObject("GazeScanpath").transform;
        path = new GameObject("ScanpathLine").AddComponent<LineRenderer>();
        path.transform.SetParent(root, false);
        path.useWorldSpace = true;
        path.widthMultiplier = lineWidth;
        path.positionCount = 0;
        path.sharedMaterial = GazeMaterials.Make(lineColor);
        path.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    private void Start() => ApplyView();

    public void Clear()
    {
        foreach (Fixation f in fixations) if (f.sphere != null) Destroy(f.sphere);
        fixations.Clear();
        path.positionCount = 0;
        EndFixation(false);
    }

    private void ApplyView()
    {
        root.gameObject.SetActive(view == View.Scanpath || view == View.Both);
        if (heatmap != null) heatmap.SetVisible(view == View.Heatmap || view == View.Both);
    }

    private void Update()
    {
        if (toggle.Down()) { view = (View)(((int)view + 1) % 4); ApplyView(); }

        bool usable = tracker != null && tracker.HasGaze && tracker.IsCalibrated && !tracker.IsCalibrating
                      && (grid == null || !grid.IsRunning);
        if (!usable)
        {
            if (inFix && Time.time - lastSampleTime > maxGapSeconds) EndFixation(true);
            IsFixating = false;
            haveLast = false;
            return;
        }

        // one sample per tracker packet (the headset runs faster than the tracker)
        bool newSample = tracker.Source != MonoGazeTracker.GazeSource.Tracker || tracker.Seq != lastSeq;
        if (!newSample) { GrowLive(); return; }
        lastSeq = tracker.Seq;

        Ray ray = tracker.GazeRayWorld;
        float now = Time.time;
        float velocity = 0f;
        if (haveLast)
        {
            float dt = Mathf.Max(now - lastSampleTime, 1e-3f);
            velocity = Vector3.Angle(lastDir, ray.direction) / dt;
        }
        bool gap = haveLast && now - lastSampleTime > maxGapSeconds;
        lastDir = ray.direction; lastSampleTime = now; haveLast = true;
        if (gap && inFix) EndFixation(true);

        bool slow = velocity < velocityThreshold;
        fastStreak = slow ? 0 : fastStreak + 1;
        if (fastStreak >= 2 && inFix) EndFixation(true);   // two fast samples in a row = saccade

        if (slow && Physics.Raycast(ray, out RaycastHit hit, maxRayDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
            && !GazeLogger.IsIgnored(hit.collider) && !hit.collider.transform.IsChildOf(root))
        {
            if (!inFix) { inFix = true; fixStart = now; fixSum = Vector3.zero; fixCount = 0; fixObject = hit.collider.name; }
            fixSum += hit.point; fixCount++;
        }
        IsFixating = inFix && now - fixStart >= minFixationSeconds;
        GrowLive();
    }

    private void GrowLive()
    {
        if (!inFix || fixCount == 0) return;
        float dur = Time.time - fixStart;
        if (dur < minFixationSeconds) return;
        if (live == null)
        {
            if (fixations.Count >= maxFixations) return;
            live = new Fixation { sphere = MakeSphere() };
            fixations.Add(live);
            path.positionCount = fixations.Count;
        }
        live.point = fixSum / fixCount;
        Vector3 towardEye = (tracker.GazeRayWorld.origin - live.point).normalized;
        live.sphere.transform.position = live.point + towardEye * 0.01f;
        float r = baseRadius + growPerSqrtSecond * Mathf.Sqrt(dur);
        live.sphere.transform.localScale = Vector3.one * r * 2f;
        GazeMaterials.SetColor(live.sphere.GetComponent<Renderer>().sharedMaterial, RampColor(dur));
        path.SetPosition(fixations.Count - 1, live.sphere.transform.position);
    }

    private void EndFixation(bool log)
    {
        if (inFix && live != null && log)
            GazeLogger.LogEvent($"fix:{fixObject}:{(Time.time - fixStart) * 1000f:F0}");
        inFix = false; live = null; fixCount = 0; IsFixating = false;
    }

    private static Color RampColor(float seconds)
    {
        for (int i = RampSeconds.Length - 1; i > 0; i--)
        {
            if (seconds >= RampSeconds[i - 1])
            {
                if (i == RampSeconds.Length) return Ramp[i - 1];
                float k = Mathf.InverseLerp(RampSeconds[i - 1], RampSeconds[i], seconds);
                return Color.Lerp(Ramp[i - 1], Ramp[i], k);
            }
        }
        return Ramp[Ramp.Length - 1];
    }

    private GameObject MakeSphere()
    {
        GameObject s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        s.name = "fixation";
        Destroy(s.GetComponent<Collider>());   // must not block gaze
        s.transform.SetParent(root, true);
        Renderer r = s.GetComponent<Renderer>();
        r.sharedMaterial = GazeMaterials.Make(Ramp[0]);
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return s;
    }
}
