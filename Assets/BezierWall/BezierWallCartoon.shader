Shader "MagicTavern/BezierWallCartoon"
{
    Properties
    {
        _MainTex ("Wall Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _TextureRect ("Sprite Texture Rect", Vector) = (0, 0, 1, 1)
        _TopTex ("Top Texture", 2D) = "white" {}
        _TopTextureRect ("Top Sprite Texture Rect", Vector) = (0, 0, 1, 1)
        [Toggle] _UseSeparateTopTex ("Use Separate Top Texture", Float) = 0
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
            sampler2D _TopTex;
            float4 _TopTex_ST;
            float4 _TopTex_TexelSize;
            float4 _TopTextureRect;
            float _UseSeparateTopTex;
            half4 _Color;

            struct Attributes
            {
                float4 position : POSITION;
                float2 uv : TEXCOORD0;
                float2 textureFace : TEXCOORD2;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 topUV : TEXCOORD1;
                half topFace : TEXCOORD2;
                half4 color : COLOR;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.position);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.topUV = TRANSFORM_TEX(input.uv, _TopTex);
                output.topFace = input.textureFace.x > 0.5 && input.textureFace.y < -0.5 ? 1.0 : 0.0;
                output.color = input.color * _Color;
                return output;
            }

            float4 SampleRepeated(sampler2D sourceTexture, float2 uv, float2 dx, float2 dy,
                float2 texelSize, float4 spriteRect)
            {
                if (any(spriteRect != float4(0.0, 0.0, 1.0, 1.0)))
                {
                    float2 inset = min(texelSize * 0.5, spriteRect.zw * 0.5);
                    float2 size = spriteRect.zw - 2.0 * inset;
                    float2 spriteUV = spriteRect.xy + inset + frac(uv) * size;
                    return tex2Dgrad(sourceTexture, spriteUV, dx * size, dy * size);
                }

                return tex2Dgrad(sourceTexture, frac(uv), dx, dy);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 dx = ddx(input.uv);
                float2 dy = ddy(input.uv);
                float2 topDx = ddx(input.topUV);
                float2 topDy = ddy(input.topUV);
                if (_UseSeparateTopTex > 0.5 && input.topFace > 0.5)
                    return SampleRepeated(_TopTex, input.topUV, topDx, topDy, _TopTex_TexelSize.xy, _TopTextureRect) * input.color;

                return SampleRepeated(_MainTex, input.uv, dx, dy, _MainTex_TexelSize.xy, _TextureRect) * input.color;
            }
            ENDCG
        }
    }
}
