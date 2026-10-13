using UnityEngine;

public static class SubdividedCubeGenerator
{
    public static Mesh Generate(int resolution, float size)
    {
        Mesh mesh = new Mesh();
        mesh.name = "Subdivided Cube";

        int verticesPerFace = (resolution + 1) * (resolution + 1);
        Vector3[] vertices = new Vector3[verticesPerFace * 6];
        int[] triangles = new int[resolution * resolution * 6 * 6];
        Vector2[] uvs = new Vector2[vertices.Length];
        Vector3[] normals = new Vector3[vertices.Length];

        int v = 0;
        int t = 0;

        v = GenerateFace(vertices, normals, uvs, triangles, resolution, size, Vector3.up, v, ref t);
        v = GenerateFace(vertices, normals, uvs, triangles, resolution, size, Vector3.down, v, ref t);
        v = GenerateFace(vertices, normals, uvs, triangles, resolution, size, Vector3.left, v, ref t);
        v = GenerateFace(vertices, normals, uvs, triangles, resolution, size, Vector3.right, v, ref t);
        v = GenerateFace(vertices, normals, uvs, triangles, resolution, size, Vector3.forward, v, ref t);
        v = GenerateFace(vertices, normals, uvs, triangles, resolution, size, Vector3.back, v, ref t);

        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        return mesh;
    }

    private static int GenerateFace(Vector3[] vertices, Vector3[] normals, Vector2[] uvs, int[] triangles, int res, float size, Vector3 localUp, int vIndex, ref int tIndex)
    {
        Vector3 axisA = new Vector3(localUp.y, localUp.z, localUp.x);
        Vector3 axisB = Vector3.Cross(localUp, axisA);

        for (int y = 0; y <= res; y++)
        {
            for (int x = 0; x <= res; x++)
            {
                int i = vIndex + x + y * (res + 1);
                Vector2 percent = new Vector2(x, y) / res;
                Vector3 pointOnUnitCube = localUp + (percent.x - 0.5f) * 2 * axisA + (percent.y - 0.5f) * 2 * axisB;
                vertices[i] = pointOnUnitCube * (size * 0.5f);
                normals[i] = localUp;
                uvs[i] = percent;

                if (x < res && y < res)
                {
                    int start = vIndex + x + y * (res + 1);
                    triangles[tIndex] = start;
                    triangles[tIndex + 1] = start + 1;
                    triangles[tIndex + 2] = start + res + 1;

                    triangles[tIndex + 3] = start + 1;
                    triangles[tIndex + 4] = start + res + 2;
                    triangles[tIndex + 5] = start + res + 1;
                    tIndex += 6;
                }
            }
        }
        return vIndex + (res + 1) * (res + 1);
    }
}