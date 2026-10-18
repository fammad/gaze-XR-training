using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Live 3D gaze heatmap: paints small discs on the surfaces you look at, coloured by how long you
/// looked there (blue = glance, red = long). Visible in the headset, so a screen recording shows it grow.
/// Right thumbstick click (Editor: H) shows/hides it. Clears itself at task_start if clearOnTaskStart.
/// Put it on the XR camera next to MonoGazeTracker.
/// </summary>
[DefaultExecutionOrder(60)]
public class GazeHeatmap : MonoBehaviour
{
    public MonoGazeTracker tracker;
    [Tooltip("Size of one heatmap cell in metres.")]
    public float cellSize = 0.03f;
    public float maxRayDistance = 20f;
    public LayerMask raycastLayers = Physics.DefaultRaycastLayers;
    public int maxCells = 3000;
    public bool visible = true;
    public bool onlyWhenCalibrated = true;
    public string toggleKey = "h";

    // Dwell (seconds) at which a cell reaches each colour.
    private static readonly float[] Thresholds = { 0f, 0.1f, 0.25f, 0.5f, 1f, 2f, 4f };
    private static readonly Color[] Colors =
    {
        new Color(0.2f, 0.4f, 1f), new Color(0.1f, 0.8f, 1f), new Color(0.2f, 1f, 0.4f),
        new Color(1f, 1f, 0.2f), new Color(1f, 0.6f, 0.1f), new Color(1f, 0.25f, 0.1f), new Color(0.8f, 0f, 0.4f)
    };

    private class Cell { public float dwell; public int level = -1; public GameObject disc; }

    private readonly Dictionary<Vector3Int, Cell> cells = new Dictionary<Vector3Int, Cell>();
    private Material[] materials;
    private Transform root;
    private XRButton toggleButton;
    private AccuracyGrid grid;
    private RaycastHit[] hits = new RaycastHit[32];

    private void Awake()
    {
        if (tracker == null) tracker = GetComponent<MonoGazeTracker>();
        if (tracker == null) tracker = FindAnyObjectByType<MonoGazeTracker>();
        grid = FindAnyObjectByType<AccuracyGrid>();
        toggleButton = new XRButton(XRNode.RightHand, CommonUsages.primary2DAxisClick, toggleKey);
        root = new GameObject("GazeHeatmap").transform;   // world space, not under the camera

        GameObject probe = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Material baseMat = probe.GetComponent<Renderer>().sharedMaterial;
        Destroy(probe);
        materials = new Material[Colors.Length];
        for (int i = 0; i < Colors.Length; i++)
        {
            materials[i] = new Material(baseMat) { color = Colors[i] };
            if (materials[i].HasProperty("_EmissionColor"))
            {
                materials[i].EnableKeyword("_EMISSION");
                materials[i].SetColor("_EmissionColor", Colors[i] * 0.6f);
            }
        }
    }

    public void Clear()
    {
        foreach (Cell c in cells.Values) if (c.disc != null) Destroy(c.disc);
        cells.Clear();
    }

    public void SetVisible(bool v)
    {
        visible = v;
        root.gameObject.SetActive(v);
    }

    private void Update()
    {
        if (toggleButton.Down()) SetVisible(!visible);
        if (tracker == null || !tracker.HasGaze) return;
        if (onlyWhenCalibrated && !tracker.IsCalibrated && tracker.Source != MonoGazeTracker.GazeSource.Mouse) return;
        if (tracker.IsCalibrating) return;
        if (grid != null && grid.IsRunning) return;   // test dots are not real looking behaviour

        if (!TryHit(tracker.GazeRayWorld, out RaycastHit hit)) return;
        Vector3Int key = Vector3Int.FloorToInt(hit.point / cellSize);
        if (!cells.TryGetValue(key, out Cell cell))
        {
            if (cells.Count >= maxCells) return;
            cell = new Cell();
            cells[key] = cell;
        }
        cell.dwell += Time.deltaTime;

        int level = 0;
        while (level + 1 < Thresholds.Length && cell.dwell >= Thresholds[level + 1]) level++;
        if (level == cell.level) return;
        cell.level = level;
        if (cell.disc == null) cell.disc = MakeDisc(hit);
        cell.disc.GetComponent<Renderer>().sharedMaterial = materials[level];
        float s = cellSize * (0.8f + 0.1f * level);
        cell.disc.transform.localScale = new Vector3(s, 0.0005f, s);
    }

    private bool TryHit(Ray ray, out RaycastHit best)
    {
        best = default;
        int n = Physics.RaycastNonAlloc(ray, hits, maxRayDistance, raycastLayers, QueryTriggerInteraction.Ignore);
        if (n == hits.Length) hits = new RaycastHit[hits.Length * 2];
        float bestDist = float.MaxValue;
        bool found = false;
        for (int i = 0; i < n; i++)
        {
            if (GazeLogger.IsIgnored(hits[i].collider) || hits[i].distance >= bestDist) continue;
            bestDist = hits[i].distance;
            best = hits[i];
            found = true;
        }
        return found;
    }

    private GameObject MakeDisc(RaycastHit hit)
    {
        GameObject disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        disc.name = "heat";
        Destroy(disc.GetComponent<Collider>());   // must not block gaze
        Renderer r = disc.GetComponent<Renderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        disc.transform.SetParent(root, false);
        disc.transform.SetPositionAndRotation(hit.point + hit.normal * 0.003f,
                                              Quaternion.FromToRotation(Vector3.up, hit.normal));
        // Discs on a held-then-dropped tool would float: attach to moving objects so they follow.
        if (hit.rigidbody != null && !hit.rigidbody.isKinematic) disc.transform.SetParent(hit.rigidbody.transform, true);
        return disc;
    }
}
