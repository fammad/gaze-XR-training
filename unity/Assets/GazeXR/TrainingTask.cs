using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Task events for the gaze log: task start/end, tool grab/release, tool-material hits, rule violations.
/// Finds every ToolCollisionDetector in the scene on Start, so no wiring per tool is needed.
/// The tool in your hand is excluded from gaze raycasts while held (it would cover every hit).
/// Start/end: left controller X (Editor: O), or call StartTask()/EndTask() from a button's UnityEvent.
/// Rule probe: the sign says "Marble: chisel only, never the hammer"; hammer on marble is logged as a violation.
/// </summary>
public class TrainingTask : MonoBehaviour
{
    public ToolCollisionDetector.ToolType forbiddenTool = ToolCollisionDetector.ToolType.Hammer;
    public string forbiddenMaterialTag = "Marble";
    public string toggleKey = "o";
    [Tooltip("Start the heatmap fresh when the task starts (calibration and grid looks are not mixed in).")]
    public bool clearHeatmapOnStart = true;

    public bool Running { get; private set; }
    private float startTime;
    private XRButton toggleButton;

    private void Start()
    {
        toggleButton = new XRButton(XRNode.LeftHand, CommonUsages.primaryButton, toggleKey);
        var tools = FindObjectsByType<ToolCollisionDetector>();
        foreach (ToolCollisionDetector tool in tools)
        {
            ToolCollisionDetector t = tool;
            t.onValidHit.AddListener(OnHit);
            XRGrabInteractable grab = t.GetComponentInParent<XRGrabInteractable>();
            if (grab == null) { Debug.LogWarning($"[Task] {t.name} has no XRGrabInteractable"); continue; }
            grab.selectEntered.AddListener(_ => OnGrab(t, grab.transform, true));
            grab.selectExited.AddListener(_ => OnGrab(t, grab.transform, false));
        }
        Debug.Log($"[Task] Watching {tools.Length} tools.");
    }

    private void Update()
    {
        if (toggleButton.Down())
        {
            if (Running) EndTask(); else StartTask();
        }
    }

    public void StartTask()
    {
        if (Running) return;
        Running = true;
        startTime = Time.time;
        GazeLogger.LogEvent("task_start");
        if (clearHeatmapOnStart)
        {
            GazeHeatmap heatmap = FindAnyObjectByType<GazeHeatmap>();
            if (heatmap != null) heatmap.Clear();
        }
        toggleButton.Buzz();
    }

    public void EndTask()
    {
        if (!Running) return;
        Running = false;
        GazeLogger.LogEvent($"task_end:{Time.time - startTime:F1}s");
        toggleButton.Buzz(0.8f, 0.3f);
    }

    private void OnGrab(ToolCollisionDetector tool, Transform root, bool grabbed)
    {
        GazeLogger.SetIgnored(root, grabbed);
        GazeLogger.LogEvent((grabbed ? "grab:" : "release:") + tool.toolType);
        if (grabbed && !Running)
        {
            StartTask();
            GazeLogger.LogEvent("task_start_auto");
        }
    }

    private void OnHit(ToolCollisionDetector.ToolHitData hit)
    {
        string material = hit.material != null ? hit.material.tag : "?";
        GazeLogger.LogEvent($"hit:{hit.tool.toolType}:{material}");
        if (hit.tool.toolType == forbiddenTool && material == forbiddenMaterialTag)
            GazeLogger.LogEvent($"rule_violation:{hit.tool.toolType}_on_{material}");
    }
}
