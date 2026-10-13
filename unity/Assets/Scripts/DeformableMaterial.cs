using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(MeshFilter))]
public class DeformableMaterial : MonoBehaviour
{
    public enum MaterialType { Wood, Metal, Marble, Clay }

    [Header("General Settings")]
    [SerializeField] private MaterialType materialType = MaterialType.Wood;
    [SerializeField] private float resistance = 1.0f;
    [SerializeField] private float maxDeformation = 0.2f;
    [SerializeField] private float deformationRadius = 0.15f;

    [Header("Mesh Settings")]
    [SerializeField] private bool autoSubdivide = true;
    [SerializeField] private int subdivisionResolution = 20;

    private Mesh mesh;
    private Vector3[] originalVertices;
    private Vector3[] modifiedVertices;

    private Dictionary<int, List<int>> vertexGroups = new Dictionary<int, List<int>>();
    private Vector3 accumulatedBendOffset = Vector3.zero;

    private bool colliderDirty;
    private float lastColliderUpdateTime;
    private const float ColliderUpdateInterval = 0.1f;

    private int bendAxisIndex;
    private int bendDirIndexA;
    private int bendDirIndexB;

    private Vector3 meshBoundsMin;
    private Vector3 meshBoundsMax;

    void Start()
    {
        MeshFilter mf = GetComponent<MeshFilter>();

        if (autoSubdivide && (mf.sharedMesh == null || mf.sharedMesh.vertexCount <= 24))
        {
            float size = mf.sharedMesh != null ? mf.sharedMesh.bounds.size.x : 1f;
            mf.mesh = SubdividedCubeGenerator.Generate(subdivisionResolution, size);
            Debug.Log($"[Deformable] Procedural subdivision generated for {gameObject.name} ({mf.mesh.vertexCount} vertices)");
        }

        mesh = mf.mesh;
        originalVertices = mesh.vertices;
        modifiedVertices = (Vector3[])originalVertices.Clone();

        meshBoundsMin = mesh.bounds.min;
        meshBoundsMax = mesh.bounds.max;

        BuildVertexGroups();
        DetectBendAxis();
    }

    private void DetectBendAxis()
    {
        Vector3 meshSize = mesh.bounds.size;
        Vector3 scale = transform.localScale;
        float wx = meshSize.x * Mathf.Abs(scale.x);
        float wy = meshSize.y * Mathf.Abs(scale.y);
        float wz = meshSize.z * Mathf.Abs(scale.z);

        if (wy >= wx && wy >= wz)
        {
            bendAxisIndex = 1;
            bendDirIndexA = 0;
            bendDirIndexB = 2;
        }
        else if (wx >= wy && wx >= wz)
        {
            bendAxisIndex = 0;
            bendDirIndexA = 1;
            bendDirIndexB = 2;
        }
        else
        {
            bendAxisIndex = 2;
            bendDirIndexA = 0;
            bendDirIndexB = 1;
        }
    }

    private void BuildVertexGroups()
    {
        vertexGroups.Clear();
        float cellSize = 0.002f;
        var spatialMap = new Dictionary<long, int>();

        for (int i = 0; i < originalVertices.Length; i++)
        {
            Vector3 v = originalVertices[i];
            long key = SpatialHash(v, cellSize);

            if (spatialMap.TryGetValue(key, out int representative))
            {
                if (Vector3.Distance(v, originalVertices[representative]) < 0.001f)
                {
                    vertexGroups[representative].Add(i);
                    continue;
                }
            }

            spatialMap[key] = i;
            vertexGroups[i] = new List<int> { i };
        }
    }

    private static long SpatialHash(Vector3 v, float cellSize)
    {
        int x = Mathf.FloorToInt(v.x / cellSize);
        int y = Mathf.FloorToInt(v.y / cellSize);
        int z = Mathf.FloorToInt(v.z / cellSize);
        return ((long)x * 73856093L) ^ ((long)y * 19349663L) ^ ((long)z * 83492791L);
    }

    void LateUpdate()
    {
        if (colliderDirty && (Time.time - lastColliderUpdateTime) >= ColliderUpdateInterval)
        {
            UpdateCollider();
            colliderDirty = false;
            lastColliderUpdateTime = Time.time;
        }
    }

    public void ApplyDeformation(Vector3 worldPoint, Vector3 worldForce, bool isFiling = false)
    {
        Vector3 impactDirection = -worldForce;
        Vector3 localPoint = transform.InverseTransformPoint(worldPoint);
        Vector3 localForce = transform.InverseTransformDirection(impactDirection);

        switch (materialType)
        {
            case MaterialType.Metal:
                ApplyMetalBending(localForce, localPoint);
                break;
            case MaterialType.Marble:
                if (isFiling) ApplyMarbleSmoothing(localPoint, localForce);
                else ApplyMarbleCracking(localPoint, localForce);
                break;
            case MaterialType.Clay:
                ApplyOrganicClayMolding(localPoint, localForce, isFiling);
                break;
            case MaterialType.Wood:
                ApplyWoodIndentation(localPoint, localForce);
                break;
        }

        mesh.vertices = modifiedVertices;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        colliderDirty = true;
    }

    private void MoveVertexGroup(int representativeIndex, Vector3 localDelta, float limitMultiplier = 1.0f, bool clampToBounds = false)
    {
        foreach (int index in vertexGroups[representativeIndex])
        {
            modifiedVertices[index] += localDelta;

            Vector3 diff = modifiedVertices[index] - originalVertices[index];
            float limit = maxDeformation * limitMultiplier;
            if (diff.magnitude > limit)
                modifiedVertices[index] = originalVertices[index] + diff.normalized * limit;

            if (clampToBounds)
            {
                Vector3 v = modifiedVertices[index];
                v.x = Mathf.Clamp(v.x, meshBoundsMin.x, meshBoundsMax.x);
                v.y = Mathf.Clamp(v.y, meshBoundsMin.y, meshBoundsMax.y);
                v.z = Mathf.Clamp(v.z, meshBoundsMin.z, meshBoundsMax.z);
                modifiedVertices[index] = v;
            }
        }
    }

    private void ApplyWoodIndentation(Vector3 localPoint, Vector3 localForce)
    {
        float forceScale = 0.015f / Mathf.Max(0.1f, resistance);
        Vector3 force = localForce * forceScale;

        foreach (var entry in vertexGroups)
        {
            int i = entry.Key;
            float dist = Vector3.Distance(localPoint, modifiedVertices[i]);
            if (dist < deformationRadius)
            {
                float falloff = Mathf.Pow((deformationRadius - dist) / deformationRadius, 2);
                MoveVertexGroup(i, force * falloff);
            }
        }
    }

    private void ApplyMetalBending(Vector3 localForce, Vector3 localImpactPoint)
    {
        Vector3 bendDir = localForce;
        bendDir[bendAxisIndex] = 0;

        Bounds b = mesh.bounds;
        float axisMin = b.min[bendAxisIndex];
        float axisMax = b.max[bendAxisIndex];
        float axisRange = axisMax - axisMin;
        if (axisRange < 0.001f) axisRange = 1f;

        float impactNormalized = Mathf.Clamp01((localImpactPoint[bendAxisIndex] - axisMin) / axisRange);
        accumulatedBendOffset += bendDir * (0.08f / resistance) * impactNormalized;

        if (accumulatedBendOffset.magnitude > maxDeformation * 5.0f)
            accumulatedBendOffset = accumulatedBendOffset.normalized * (maxDeformation * 5.0f);

        foreach (var entry in vertexGroups)
        {
            int i = entry.Key;
            float h = (originalVertices[i][bendAxisIndex] - axisMin) / axisRange;
            float weight = Mathf.Pow(Mathf.Clamp01(h), 3.0f);

            Vector3 targetPos = originalVertices[i] + accumulatedBendOffset * weight;
            Vector3 delta = targetPos - modifiedVertices[i];
            MoveVertexGroup(i, delta, 5.0f);
        }
    }

    private void ApplyMarbleCracking(Vector3 localPoint, Vector3 localForce)
    {
        float forceScale = 0.04f / Mathf.Max(0.1f, resistance);
        Vector3 force = localForce.normalized * forceScale;

        foreach (var entry in vertexGroups)
        {
            int i = entry.Key;
            float dist = Vector3.Distance(localPoint, modifiedVertices[i]);
            if (dist < deformationRadius * 0.6f)
            {
                float falloff = 1f - (dist / (deformationRadius * 0.6f));
                float noise = Random.Range(0.6f, 1.4f);
                Vector3 jitter = Random.onUnitSphere * 0.003f;
                MoveVertexGroup(i, (force * falloff + jitter) * noise, 1.0f, true);
            }
        }
    }

    private void ApplyMarbleSmoothing(Vector3 localPoint, Vector3 localForce)
    {
        foreach (var entry in vertexGroups)
        {
            int i = entry.Key;
            float dist = Vector3.Distance(localPoint, modifiedVertices[i]);
            if (dist < deformationRadius)
            {
                foreach (int index in entry.Value)
                    modifiedVertices[index] = Vector3.Lerp(modifiedVertices[index], originalVertices[index], 0.25f);
            }
        }
    }

    private void ApplyOrganicClayMolding(Vector3 localPoint, Vector3 localForce, bool isFiling)
    {
        float forceScale = (isFiling ? 0.12f : 0.08f) / Mathf.Max(0.1f, resistance);
        Vector3 force = localForce * forceScale;

        foreach (var entry in vertexGroups)
        {
            int i = entry.Key;
            float dist = Vector3.Distance(localPoint, modifiedVertices[i]);
            if (dist < deformationRadius)
            {
                float t = dist / deformationRadius;
                float bulge = Mathf.Sin(t * Mathf.PI);
                float push = Mathf.Cos(t * Mathf.PI * 0.5f);

                Vector3 outward = (modifiedVertices[i] - localPoint).normalized * bulge * force.magnitude * 0.15f;
                MoveVertexGroup(i, (force * push) + outward, 1.5f);
            }
        }
    }

    private void UpdateCollider()
    {
        MeshCollider mc = GetComponent<MeshCollider>();
        if (mc != null)
        {
            mc.sharedMesh = null;
            mc.sharedMesh = mesh;
            if (!mc.convex) mc.convex = true;
        }
        else
        {
            mc = gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = true;
        }
    }
}
