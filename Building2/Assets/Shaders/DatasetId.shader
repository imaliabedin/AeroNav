// Label mask pass for the YOLO dataset generator: every pixel of an object gets the object's id,
// packed as 8 levels per channel (id = r + 8g + 64b, each channel = level / 7). The coarse levels
// survive any 8-bit / sRGB round trip URP may make on the way to the render texture.
Shader "Hidden/DatasetGen/Id"
{
    Properties
    {
        _IdColor ("Id color (packed, raw values)", Vector) = (0, 0, 0, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Pass
        {
            Name "Id"
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _IdColor;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return o;
            }

            float4 frag(Varyings input) : SV_Target
            {
                return float4(_IdColor.rgb, 1);
            }
            ENDHLSL
        }
    }
}
