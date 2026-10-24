// Draws the mask being painted over the model in the Scene view. Overlay (_Mode 0): white parts green, black parts
// slightly darkened. Mask only (_Mode 1): the mask's own greys, unlit and opaque. The piece under the mouse is
// lighter. Pass 0 draws parts that are covered (by clothes, say) faintly, so painting under clothes stays visible;
// pass 1 draws the visible parts. Only the Mask Painter window draws it, never a material on the avatar.
Shader "Hidden/DocMe/MaskPainter/Overlay"
{
    Properties
    {
        _Mask ("Mask", 2D) = "black" {}
        _Tint ("Tint", Color) = (0.3, 0.85, 0.4, 1)
        _Opacity ("Opacity", Range(0, 1)) = 0.6
        _Hover ("Piece under the mouse", Float) = -1
        _Mode ("Overlay or mask only", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        CGINCLUDE
            #include "UnityCG.cginc"

            sampler2D _Mask;
            float4 _Tint;
            float _Opacity;
            float _Hover;
            float _Mode;

            // The mask's value as a grey the way an image viewer shows its PNG: in a linear project the Scene view's
            // target turns what is written into sRGB, so the value goes in as linear first.
            float3 MaskGrey(float m)
            {
            #if defined(UNITY_COLORSPACE_GAMMA)
                return m;
            #else
                return GammaToLinearSpace(float3(m, m, m));
            #endif
            }

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float2 island : TEXCOORD1;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                nointerpolation float island : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.island = v.island.x;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float m = tex2D(_Mask, i.uv).r;
                float hover = abs(i.island - _Hover) < 0.5 ? 1.0 : 0.0;
                float3 col = _Tint.rgb * m;
                float alpha = lerp(0.3, 1.0, m) * _Opacity;
                if (_Mode > 0.5)
                {
                    col = MaskGrey(m);
                    alpha = 1.0;
                }
                col = lerp(col, _Mode > 0.5 ? _Tint.rgb : 1.0, hover * 0.35);
                alpha = max(alpha, hover * 0.5);
                return float4(col, alpha);
            }

            float4 fragCovered(v2f i) : SV_Target
            {
                float m = tex2D(_Mask, i.uv).r;
                float hover = abs(i.island - _Hover) < 0.5 ? 1.0 : 0.0;
                float3 col = lerp(_Tint.rgb, 1.0, hover * 0.35);
                float alpha = max(m, hover) * _Opacity * 0.25;
                if (_Mode > 0.5)
                {
                    col = lerp(MaskGrey(m), _Tint.rgb, hover * 0.35);
                    alpha = 0.35;
                }
                return float4(col, alpha);
            }
        ENDCG

        Pass
        {
            ZWrite Off
            ZTest Greater
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragCovered
            ENDCG
        }

        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Off
            Offset -1, -1
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            ENDCG
        }
    }
}
