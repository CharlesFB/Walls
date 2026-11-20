Shader "MagicTavern/BezierWallCartoon"
{
    Properties
    {
        _MainTex ("Wall Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _TextureRect ("Sprite Texture Rect", Vector) = (0, 0, 1, 1)
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _MainTex_TexelSize;
            float4 _TextureRect;
            half4 _Color;

            struct Attributes
            {
                float4 position : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.position);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.color = input.color * _Color;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                if (any(_TextureRect != float4(0.0, 0.0, 1.0, 1.0)))
                {
                    float2 inset = min(_MainTex_TexelSize.xy * 0.5, _TextureRect.zw * 0.5);
                    float2 size = _TextureRect.zw - 2.0 * inset;
                    float2 uv = _TextureRect.xy + inset + frac(input.uv) * size;
                    return tex2Dgrad(_MainTex, uv, ddx(input.uv) * size, ddy(input.uv) * size) * input.color;
                }

                return tex2Dgrad(_MainTex, frac(input.uv), ddx(input.uv), ddy(input.uv)) * input.color;
            }
            ENDCG
        }
    }
}
