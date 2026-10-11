using UnityEngine;

/// Shared look for the scene overlay lines (scan region, scan route, sonar rings): fully opaque at ground level,
/// fading linearly to transparent at `Height` m above it, and hidden behind buildings (depth-tested, unlike the old
/// always-on-top overlay). The fade is baked into vertex alpha, so lines need enough vertices along their height
/// (straight vertical lines and quads need only their two ends: alpha interpolates smoothly between them).
public static class HeightFade
{
    public const float GroundY = -0.5f;
    public static float Height = 10f;

    public static float Alpha(float y) => 1f - Mathf.Clamp01((y - GroundY) / Height);

    public static Color At(Color c, float y) { c.a *= Alpha(y); return c; }

    static Material shared;

    /// Vertex-colored, alpha-blended, depth-tested (no depth writes), drawn after opaque geometry.
    public static Material SharedMaterial
    {
        get
        {
            if (shared != null) return shared;
            // From Resources so the shader is included in player builds (WebGL strips unreferenced shaders).
            var template = Resources.Load<Material>("SonarLines");
            shared = template ? new Material(template) : new Material(Shader.Find("Hidden/Internal-Colored"));
            shared.name = "HeightFadeLines";
            shared.hideFlags = HideFlags.HideAndDontSave;
            shared.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            shared.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            shared.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            shared.SetInt("_ZWrite", 0);
            shared.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            shared.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            return shared;
        }
    }
}
