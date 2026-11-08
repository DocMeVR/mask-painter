// Draws the mask being painted over the model in the Scene view. Overlay (_Mode 0): the painted channel (_Channel 0 to
// 3 for red, green, blue, alpha) in its tint, black parts slightly darkened. Every channel (_Every 1): red, green and blue
// over the model as the colours they are. Mask only (_Mode 1): the painted channel's greys, unlit and opaque. The piece
// under the mouse is lighter. Pass 0 draws parts that are covered (by clothes, say) faintly, so painting under clothes
// stays visible; pass 1 draws the visible parts. Pass 2 draws the window's UV view: the mask as a flat picture with the
// UV layout's lines (_Wire) and a ring at the mouse (_HoverUv). Only the Mask Painter window draws it, never a material
// on the avatar.
Shader "Hidden/DocMe/MaskPainter/Overlay"
{
    Properties
    {
        _Mask ("Mask", 2D) = "black" {}
        _Tint ("Tint", Color) = (0.25, 0.79, 0.66, 1)
        _Opacity ("Opacity", Range(0, 1)) = 0.6
        _Hover ("Piece under the mouse", Float) = -1
        _Mode ("Overlay or mask only", Float) = 0
        _Channel ("Painted channel", Float) = 0
        _Every ("Every channel", Float) = 0
        _MainTex ("UV view source", 2D) = "black" {}
        _Wire ("UV layout lines", 2D) = "black" {}
        _HoverUv ("Mouse on the UV map", Vector) = (0, 0, 0, 0)
        _ViewSize ("UV view size in pixels", Vector) = (512, 512, 0, 0)
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
            float _Channel;
            float _Every;
            sampler2D _MainTex;
            sampler2D _Wire;
            float4 _HoverUv;
            float4 _ViewSize;

            // The mask's value as a grey the way an image viewer shows its PNG: in a linear project the Scene view's
            // target turns what is written into sRGB, so the value goes in as linear first.
            float3 MaskGrey(float3 m)
            {
            #if defined(UNITY_COLORSPACE_GAMMA)
                return m;
            #else
                return GammaToLinearSpace(m);
            #endif
            }

            float Painted(float4 t)
            {
                return _Channel < 0.5 ? t.r : _Channel < 1.5 ? t.g : _Channel < 2.5 ? t.b : t.a;
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
                float4 t = tex2D(_Mask, i.uv);
                float m = Painted(t);
                float hover = abs(i.island - _Hover) < 0.5 ? 1.0 : 0.0;
                float3 col = _Tint.rgb * m;
                float alpha = lerp(0.3, 1.0, m) * _Opacity;
                if (_Every > 0.5)
                {
                    col = MaskGrey(t.rgb);
                    alpha = lerp(0.3, 1.0, max(t.r, max(t.g, t.b))) * _Opacity;
                }
                if (_Mode > 0.5)
                {
                    col = MaskGrey(m.xxx);
                    alpha = 1.0;
                }
                col = lerp(col, _Mode > 0.5 ? _Tint.rgb : 1.0, hover * 0.35);
                alpha = max(alpha, hover * 0.5);
                return float4(col, alpha);
            }

            float4 fragCovered(v2f i) : SV_Target
            {
                float4 t = tex2D(_Mask, i.uv);
                float m = Painted(t);
                float hover = abs(i.island - _Hover) < 0.5 ? 1.0 : 0.0;
                float3 col = lerp(_Tint.rgb, 1.0, hover * 0.35);
                float alpha = max(m, hover) * _Opacity * 0.25;
                if (_Every > 0.5)
                {
                    float most = max(t.r, max(t.g, t.b));
                    col = lerp(MaskGrey(t.rgb), 1.0, hover * 0.35);
                    alpha = max(most, hover) * _Opacity * 0.25;
                }
                if (_Mode > 0.5)
                {
                    col = lerp(MaskGrey(m.xxx), _Tint.rgb, hover * 0.35);
                    alpha = 0.35;
                }
                return float4(col, alpha);
            }
            float4 fragView(v2f_img i) : SV_Target
            {
                float4 t = tex2D(_MainTex, i.uv);
                float3 col = _Every > 0.5 ? t.rgb : Painted(t).xxx;
                float3 tint = _Tint.rgb;
            #if !defined(UNITY_COLORSPACE_GAMMA)
                tint = LinearToGammaSpace(tint);
            #endif
                col = lerp(col, tint, tex2D(_Wire, i.uv).r * 0.75);
                if (_HoverUv.z > 0.5)
                {
                    float d = length((i.uv - _HoverUv.xy) * _ViewSize.xy);
                    col = lerp(col, 0.0, saturate(2.0 - abs(d - 6.0)));
                    col = lerp(col, 1.0, saturate(1.2 - abs(d - 6.0)));
                }
                return float4(MaskGrey(col), 1.0);
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

        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment fragView
            ENDCG
        }
    }
}
