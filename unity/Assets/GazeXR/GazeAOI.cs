using UnityEngine;

/// <summary>
/// Marks an object as an area of interest (AOI). Gaze rays that hit any collider on this object
/// or its children are logged with this name and category. The object needs a collider.
/// </summary>
public class GazeAOI : MonoBehaviour
{
    public enum Category { Instruction, Safety, Control, Material, Tool, Other }

    [Tooltip("Name in the log. Empty = GameObject name.")]
    public string aoiName = "";
    public Category category = Category.Other;

    public string Name => string.IsNullOrEmpty(aoiName) ? gameObject.name : aoiName;

    private void Reset()
    {
        if (GetComponentInChildren<Collider>() == null)
            Debug.LogWarning($"[GazeAOI] {name} has no collider; gaze can never hit it.");
    }
}
