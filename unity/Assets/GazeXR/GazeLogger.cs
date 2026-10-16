using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Writes one CSV row per frame: head pose, eye gaze, which AOI eye gaze and head gaze hit, and task events.
/// File: Application.persistentDataPath/sessions/session_yyyyMMdd_HHmmss.csv
/// On the Quest: /sdcard/Android/data/[package id]/files/sessions/
/// Put it on the XR camera next to MonoGazeTracker.
/// </summary>
[DefaultExecutionOrder(50)]
public class GazeLogger : MonoBehaviour
{
    public MonoGazeTracker tracker;
    public float maxRayDistance = 20f;
    public LayerMask raycastLayers = Physics.DefaultRaycastLayers;
    public bool logToFile = true;

    public static string CurrentFile { get; private set; }

    private static readonly List<string> pendingEvents = new List<string>();
    private static readonly HashSet<Collider> ignored = new HashSet<Collider>();

    private StreamWriter writer;
    private float nextFlush;
    private readonly StringBuilder row = new StringBuilder(256);
    private RaycastHit[] hits = new RaycastHit[32];

    /// <summary>Adds an event to the next logged row (e.g. "grab:Hammer").</summary>
    public static void LogEvent(string evt)
    {
        lock (pendingEvents) pendingEvents.Add(evt.Replace(',', ' ').Replace(';', ' '));
        Debug.Log("[GazeLog] " + evt);
    }

    /// <summary>Exclude an object (e.g. the tool in your hand) from gaze raycasts, or include it again.</summary>
    public static void SetIgnored(Transform root, bool ignore)
    {
        foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
        {
            if (ignore) ignored.Add(c); else ignored.Remove(c);
        }
    }

    /// <summary>True if this collider is excluded from gaze raycasts (e.g. the held tool).</summary>
    public static bool IsIgnored(Collider c) => ignored.Contains(c);

    private void Awake()
    {
        if (tracker == null) tracker = GetComponent<MonoGazeTracker>();
        if (tracker == null) tracker = FindFirstObjectByType<MonoGazeTracker>();
    }

    private void OnEnable()
    {
        if (!logToFile) return;
        string dir = Path.Combine(Application.persistentDataPath, "sessions");
        Directory.CreateDirectory(dir);
        CurrentFile = Path.Combine(dir, "session_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");
        writer = new StreamWriter(CurrentFile, false, new UTF8Encoding(false));
        writer.WriteLine("time,frame,head_px,head_py,head_pz,head_fx,head_fy,head_fz," +
                         "gaze_source,calibrated,seq,confidence,eye_ox,eye_oy,eye_oz,eye_dx,eye_dy,eye_dz," +
                         "eye_aoi,eye_aoi_category,eye_hit_distance,head_aoi,head_aoi_category,head_hit_distance,events");
        Debug.Log("[GazeLog] Logging to " + CurrentFile);
        LogEvent("log_start");
    }

    private void OnDisable() => Close();
    private void OnApplicationQuit() => Close();
    private void OnApplicationPause(bool paused) { if (paused) writer?.Flush(); }

    private void Close()
    {
        if (writer == null) return;
        writer.Flush();
        writer.Close();
        writer = null;
    }

    private void LateUpdate()
    {
        if (writer == null) return;
        var inv = CultureInfo.InvariantCulture;
        Transform head = transform;
        Vector3 hp = head.position, hf = head.forward;

        row.Clear();
        row.Append(Time.timeAsDouble.ToString("F4", inv)).Append(',').Append(Time.frameCount).Append(',');
        AppendVec(hp); AppendVec(hf);

        bool hasGaze = tracker != null && tracker.HasGaze;
        row.Append(tracker != null ? tracker.Source.ToString() : "None").Append(',');
        row.Append(tracker != null && tracker.IsCalibrated ? 1 : 0).Append(',');
        row.Append(tracker != null ? tracker.Seq : -1).Append(',');
        row.Append((hasGaze ? tracker.Confidence : 0f).ToString("F3", inv)).Append(',');
        if (hasGaze) { AppendVec(tracker.GazeRayWorld.origin); AppendVec(tracker.GazeRayWorld.direction); }
        else row.Append(",,,,,,");

        if (hasGaze) AppendHit(tracker.GazeRayWorld); else row.Append(",,,");
        AppendHit(new Ray(hp, hf));

        lock (pendingEvents)
        {
            row.Append(string.Join(";", pendingEvents));
            pendingEvents.Clear();
        }
        writer.WriteLine(row.ToString());

        if (Time.unscaledTime >= nextFlush)
        {
            writer.Flush();
            nextFlush = Time.unscaledTime + 1f;
        }
    }

    private void AppendVec(Vector3 v)
    {
        var inv = CultureInfo.InvariantCulture;
        row.Append(v.x.ToString("F4", inv)).Append(',')
           .Append(v.y.ToString("F4", inv)).Append(',')
           .Append(v.z.ToString("F4", inv)).Append(',');
    }

    /// <summary>Nearest hit that is not ignored. Writes aoi name, category, distance (3 columns).</summary>
    private void AppendHit(Ray ray)
    {
        int n = Physics.RaycastNonAlloc(ray, hits, maxRayDistance, raycastLayers, QueryTriggerInteraction.Collide);
        if (n == hits.Length) hits = new RaycastHit[hits.Length * 2];
        float best = float.MaxValue;
        Collider bestCollider = null;
        for (int i = 0; i < n; i++)
        {
            if (ignored.Contains(hits[i].collider) || hits[i].distance >= best) continue;
            best = hits[i].distance;
            bestCollider = hits[i].collider;
        }
        if (bestCollider == null) { row.Append(",,,"); return; }
        GazeAOI aoi = bestCollider.GetComponentInParent<GazeAOI>();
        row.Append(aoi != null ? aoi.Name.Replace(',', ' ') : "").Append(',')
           .Append(aoi != null ? aoi.category.ToString() : "").Append(',')
           .Append(best.ToString("F3", CultureInfo.InvariantCulture)).Append(',');
    }
}
