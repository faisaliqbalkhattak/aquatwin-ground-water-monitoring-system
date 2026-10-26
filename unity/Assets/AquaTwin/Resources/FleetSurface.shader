Shader "AquaTwin/FleetSurface"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _VertexTint ("Use vertex color", Float) = 0
        _Lit ("Lighting", Float) = 1
        _ZWrite ("Depth write", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite [_ZWrite]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Input { float4 vertex : POSITION; float3 normal : NORMAL; float4 color : COLOR; };
            struct Varying { float4 vertex : SV_POSITION; float3 normal : TEXCOORD0; float4 color : COLOR; };
            fixed4 _Color;
            float _Lit, _VertexTint;
            Varying vert(Input v)
            {
                Varying o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.color = lerp(float4(1,1,1,1), v.color, _VertexTint);
                return o;
            }
            fixed4 frag(Varying v) : SV_Target
            {
                float diffuse = .45 + .55 * max(0, dot(normalize(v.normal), normalize(float3(-.4,.8,-.5))));
                return fixed4(_Color.rgb * v.color.rgb * lerp(1, diffuse, _Lit), _Color.a * v.color.a);
            }
            ENDCG
        }
    }
}
