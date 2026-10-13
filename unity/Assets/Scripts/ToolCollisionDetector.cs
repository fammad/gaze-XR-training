using UnityEngine;
using UnityEngine.Events;
using System.Collections.Generic;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Collider))]
public class ToolCollisionDetector : MonoBehaviour
{
    public enum ToolType { Hammer, Chisel, File, Saw }

    public struct ToolHitData
    {
        public ToolCollisionDetector tool;
        public GameObject material;
        public float strength;
        public Vector3 point;
        public Vector3 velocity;
    }

    [Header("Configuration")]
    public ToolType toolType;
    [SerializeField] private float hitCooldown = 0.2f;
    [SerializeField] private float minVelocityThreshold = 0.1f;

    [Header("Events")]
    public UnityEvent<ToolHitData> onValidHit = new UnityEvent<ToolHitData>();
    public UnityEvent<ToolCollisionDetector, GameObject, float, Vector3> onValidStay = new UnityEvent<ToolCollisionDetector, GameObject, float, Vector3>();
    public UnityEvent onValidExit = new UnityEvent();

    private Dictionary<int, float> lastHitTimes = new Dictionary<int, float>();

    private void OnCollisionEnter(Collision collision)
    {
        string materialTag = collision.gameObject.tag;
        if (!IsValidMaterial(materialTag)) return;

        int instanceId = collision.gameObject.GetInstanceID();
        if (IsOnCooldown(instanceId)) return;

        float hitStrength = collision.relativeVelocity.magnitude;
        if (hitStrength < minVelocityThreshold) return;

        lastHitTimes[instanceId] = Time.time;

        ToolHitData data = new ToolHitData
        {
            tool = this,
            material = collision.gameObject,
            strength = hitStrength,
            point = collision.contacts[0].point,
            velocity = collision.relativeVelocity
        };

        onValidHit?.Invoke(data);
        
        Debug.Log($"[ToolHit] {toolType} struck {materialTag} with strength: {hitStrength:F2}");
    }

    private void OnCollisionStay(Collision collision)
    {
        if (toolType != ToolType.Saw && toolType != ToolType.File) return;

        string materialTag = collision.gameObject.tag;
        if (!IsValidMaterial(materialTag)) return;

        float speed = collision.relativeVelocity.magnitude;
        if (speed < 0.01f) return;

        onValidStay?.Invoke(this, collision.gameObject, speed, collision.contacts[0].point);
    }

    private void OnCollisionExit(Collision collision)
    {
        string materialTag = collision.gameObject.tag;
        if (!IsValidMaterial(materialTag)) return;

        onValidExit?.Invoke();
    }

    private bool IsValidMaterial(string tag)
    {
        return tag == "Wood" || tag == "Metal" || tag == "Clay" || tag == "Marble";
    }

    private bool IsOnCooldown(int id)
    {
        if (lastHitTimes.TryGetValue(id, out float lastTime))
        {
            return (Time.time - lastTime) < hitCooldown;
        }
        return false;
    }
}
