using System.Collections;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// One-button study session, so a solo user does not have to remember the order:
///   calibrate (redo once if the error is too big) -> accuracy grid -> task -> accuracy grid (drift).
/// Right controller A (Editor: P) starts it. During the task, A ends the task.
/// Only the right controller is needed: A = session, B = calibration only, thumbstick click = heatmap.
/// Every phase is written to the gaze log as an event, so the log can be cut into phases later.
/// Put it on the Main Camera next to MonoGazeTracker and AccuracyGrid.
/// </summary>
public class SessionRunner : MonoBehaviour
{
    public MonoGazeTracker tracker;
    public AccuracyGrid grid;
    [Tooltip("Object with the TrainingTask component (StartTask / EndTask are called on it).")]
    public GameObject trainingTask;
    [Tooltip("Calibration is repeated once if its mean error is above this (degrees).")]
    public float maxCalibrationError = 4f;
    public string startKey = "p";

    public bool IsRunning { get; private set; }

    private XRButton button;
    private bool endTaskRequested;

    private void Awake()
    {
        if (tracker == null) tracker = GetComponent<MonoGazeTracker>();
        if (grid == null) grid = GetComponent<AccuracyGrid>();
        if (trainingTask == null)
        {
            TrainingTask t = FindAnyObjectByType<TrainingTask>();
            if (t != null) trainingTask = t.gameObject;
        }
        button = new XRButton(XRNode.RightHand, CommonUsages.primaryButton, startKey);
    }

    private void Update()
    {
        if (!button.Down()) return;
        if (!IsRunning) StartCoroutine(Run());
        else endTaskRequested = true;
    }

    private IEnumerator Run()
    {
        IsRunning = true;
        GazeLogger.LogEvent("session_start");

        // 1. calibration, repeated once if poor
        for (int attempt = 0; attempt < 2; attempt++)
        {
            GazeLogger.LogEvent($"phase:calibration:{attempt + 1}");
            tracker.StartCalibration();
            yield return null;
            while (tracker.IsCalibrating) yield return null;
            yield return new WaitForSeconds(tracker.resultSeconds);   // let the user read the result
            if (tracker.IsCalibrated && tracker.LastCalibrationError <= maxCalibrationError) break;
            if (attempt == 0) { tracker.ShowHud("Error too big, calibrating once more.", 2.5f); yield return new WaitForSeconds(2.5f); }
        }

        // 2. accuracy grid before the task
        yield return RunGrid("grid_pre", "Accuracy check: look at each dot.");

        // 3. task
        GazeLogger.LogEvent("phase:task");
        tracker.ShowHud("Do the task.\nPress A when you are done.", 4f);
        if (trainingTask != null) trainingTask.SendMessage("StartTask", SendMessageOptions.DontRequireReceiver);
        endTaskRequested = false;
        while (!endTaskRequested) yield return null;
        if (trainingTask != null) trainingTask.SendMessage("EndTask", SendMessageOptions.DontRequireReceiver);

        // 4. accuracy grid after the task (drift)
        yield return RunGrid("grid_post", "Last check: look at each dot.");

        GazeLogger.LogEvent("session_end");
        tracker.ShowHud("Session done. Thank you!", 4f);
        IsRunning = false;
    }

    private IEnumerator RunGrid(string phase, string message)
    {
        GazeLogger.LogEvent($"phase:{phase}");
        tracker.ShowHud(message, 2.5f);
        yield return new WaitForSeconds(2.5f);
        grid.StartGrid();
        yield return null;
        while (grid.IsRunning) yield return null;
    }
}
