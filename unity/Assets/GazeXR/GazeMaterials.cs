using UnityEngine;

/// <summary>
/// Unlit materials for the gaze visuals. GameObject.CreatePrimitive gives the built-in default
/// material, which URP cannot draw in a Quest build (it shows magenta). URP/Unlit is listed under
/// Always Included Shaders in Graphics settings so Shader.Find works in the build.
/// </summary>
public static class GazeMaterials
{
    private static Shader shader;

    public static Material Make(Color c)
    {
        if (shader == null)
        {
            shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Color");
        }
        Material m = new Material(shader);
        SetColor(m, c);
        return m;
    }

    public static void SetColor(Material m, Color c)
    {
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
    }
}
