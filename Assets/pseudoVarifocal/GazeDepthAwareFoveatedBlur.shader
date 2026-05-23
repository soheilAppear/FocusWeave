

////// before cleanup 1/17/2026

Shader "Hidden/GazeDepthAwareFoveatedBlur"
{
    Properties
    {
        _MainTex ("MainTex", 2D) = "white" {} // Source image
        _BlurTex ("BlurTex", 2D) = "white" {} // Mipmapped blur image
    }

    SubShader
    {
        Cull Off            // Fullscreen quad
        ZWrite Off          // Do not write depth
        ZTest Always        // Always pass

        Pass
        {
            CGPROGRAM
            #pragma vertex vert          // Stereo-aware fullscreen vertex
            #pragma fragment frag        // Fragment shader
            #pragma target 3.0           // Needed for tex2Dlod
            #pragma multi_compile_instancing
            #pragma multi_compile __ CHROMABLUR_ON

            #include "UnityCG.cginc"     // Unity helpers

            struct appdata
            {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;          // Sharp source
            sampler2D _BlurTex;          // Blur texture with mip chain
            float4 _MainTex_TexelSize;   // Source texel size for direct blur

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture); // Camera depth texture

            float4 _GazeUV;              // xy = left/mono gaze UV, zw = right gaze UV

            float _FocusMeters;          // Focus plane distance
            float _FoveaInnerDeg;        // Inner fovea angle
            float _FoveaOuterDeg;        // Outer fovea angle

            float _DefocusAtMaxBlurDiopters; // Normalize defocus into 0..1
            float _BlurStrength;             // Global strength multiplier
            float _Engage01;                 // 0..1 ramp

            float _TanHalfFovX;          // tan(FOVx/2)
            float _TanHalfFovY;          // tan(FOVy/2)

            float _MaxMip;               // Maximum mip to sample

            float _BasePeripheryBlur;    // Base blur
            float _DepthBlurWeight;      // Depth defocus contribution
            float _UseDirectBlur;        // 1 = use direct multi-tap blur
            float _DirectBlurRadiusPixels;// Direct blur radius

            float _DebugGazeDot;         // 0 or 1 for drawing dot
            float _DotRadiusUV;          // Dot radius in UV

            // Chromatic aberration uniforms (Thibos LCA model)
            float _ChromaticOffsetR;     // Per-channel dioptric offset: R (default -0.4 D)
            float _ChromaticOffsetG;     // Per-channel dioptric offset: G (default  0.0 D)
            float _ChromaticOffsetB;     // Per-channel dioptric offset: B (default +1.0 D)
            float _ChromaticBlurStrength;// Scales diopters -> mip level (default 1.0)
            float _MaxChromaticMip;      // Upper mip clamp (default 6)
            float _ChromaticFovealWeight;// Chromatic blur scale at foveal centre (default 0.5)

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            float2 StereoTextureUV(float2 eyeUV)
            {
            #if defined(UNITY_SINGLE_PASS_STEREO)
                return UnityStereoTransformScreenSpaceTex(eyeUV);
            #else
                return eyeUV;
            #endif
            }

            float2 ClampEyeUV(float2 eyeUV)
            {
                return clamp(eyeUV, float2(0.001, 0.001), float2(0.999, 0.999));
            }

            float2 CurrentEyeGazeUV()
            {
            #if defined(UNITY_SINGLE_PASS_STEREO) || defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                return (unity_StereoEyeIndex == 0) ? _GazeUV.xy : _GazeUV.zw;
            #else
                return _GazeUV.xy;
            #endif
            }

            fixed4 SampleSharp(float2 uv)
            {
                return tex2D(_MainTex, StereoTextureUV(ClampEyeUV(uv)));
            }

            fixed4 SampleDirectBlur(float2 uv, float blur01)
            {
                float radius = max(1.0, _DirectBlurRadiusPixels) * saturate(blur01);
                float2 px = _MainTex_TexelSize.xy * radius;

                fixed4 c = SampleSharp(uv) * 0.18;
                c += SampleSharp(uv + float2( px.x, 0.0)) * 0.10;
                c += SampleSharp(uv + float2(-px.x, 0.0)) * 0.10;
                c += SampleSharp(uv + float2(0.0,  px.y)) * 0.10;
                c += SampleSharp(uv + float2(0.0, -px.y)) * 0.10;
                c += SampleSharp(uv + float2( px.x,  px.y)) * 0.08;
                c += SampleSharp(uv + float2(-px.x,  px.y)) * 0.08;
                c += SampleSharp(uv + float2( px.x, -px.y)) * 0.08;
                c += SampleSharp(uv + float2(-px.x, -px.y)) * 0.08;

                float2 px2 = px * 1.85;
                c += SampleSharp(uv + float2( px2.x, 0.0)) * 0.05;
                c += SampleSharp(uv + float2(-px2.x, 0.0)) * 0.05;

                return c;
            }

            // Convert angular eccentricity from gaze center
            float EccentricityDeg(float2 uv, float2 gazeUV)
            {
                float2 d = (uv - gazeUV) * 2.0;                   // Map viewport to [-1..1]
                float2 t = float2(d.x * _TanHalfFovX, d.y * _TanHalfFovY); // Scale by tan(FOV/2)
                float ang = atan(length(t));                       // Angular distance
                return degrees(ang);                               // Return degrees
            }

            // Convert depth texture to linear eye depth
            float LinearEyeDepthMeters(float2 uv)
            {
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv); // Sample depth
                return max(1e-4, LinearEyeDepth(raw));                     // Linearize
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float2 uv = i.uv;                 // Current pixel UV inside this eye
                float2 texUV = StereoTextureUV(ClampEyeUV(uv)); // Source texture UV for current eye
                float2 gazeUV = CurrentEyeGazeUV(); // Gaze center for current eye

                fixed4 sharpCol = SampleSharp(uv);        // Sample sharp
                fixed4 outCol = sharpCol;              // Default output is sharp

                // Only do blur if engaged
                if (_Engage01 > 0.0001)
                {
                    float eccDeg = EccentricityDeg(uv, gazeUV); // Angular eccentricity

                    // Fovea mask based on angular eccentricity (smoothstep-like)
                    float t = saturate((eccDeg - _FoveaInnerDeg) / max(1e-5, (_FoveaOuterDeg - _FoveaInnerDeg)));
                    float foveaMask = t * t * (3.0 - 2.0 * t); // Smoothstep curve

                    // Depth-based defocus
                    float z  = LinearEyeDepthMeters(texUV);    // Depth at pixel
                    float zf = max(1e-4, _FocusMeters);         // Focus depth

                    float defocusDiopters = abs((1.0 / z) - (1.0 / zf)); // Diopters difference
                    float defocusN = saturate(defocusDiopters / max(1e-5, _DefocusAtMaxBlurDiopters)); // Normalize

                    float blurFactor = saturate(_BasePeripheryBlur + _DepthBlurWeight * defocusN); // Combine periphery + depth

                    float blur = saturate(foveaMask * blurFactor * _BlurStrength) * saturate(_Engage01); // Final blur [0..1]

                    if (blur > 0.0001)
                    {
                        fixed4 blurCol;

                        if (_UseDirectBlur > 0.5)
                        {
                            // Direct multi-tap path: no per-channel split (fallback quality mode)
                            blurCol = SampleDirectBlur(uv, blur);
                            outCol = lerp(sharpCol, blurCol, blur);
                        }
                        else
                        {
#if CHROMABLUR_ON
                            // Signed defocus in diopters (+ = pixel nearer than focus plane)
                            float D_defocus = (1.0 / z) - (1.0 / zf);

                            // Per-channel effective defocus: Thibos reduced chromatic eye model
                            float D_R = D_defocus + _ChromaticOffsetR;
                            float D_G = D_defocus + _ChromaticOffsetG;
                            float D_B = D_defocus + _ChromaticOffsetB;

                            // Reduce chromatic blur at foveal centre so foveated sharpness is preserved
                            float fovealScale = lerp(_ChromaticFovealWeight, 1.0, foveaMask);

                            // Map |defocus| * strength to mip level, clamped
                            float mipR = clamp(abs(D_R) * _ChromaticBlurStrength * fovealScale, 0.0, _MaxChromaticMip);
                            float mipG = clamp(abs(D_G) * _ChromaticBlurStrength * fovealScale, 0.0, _MaxChromaticMip);
                            float mipB = clamp(abs(D_B) * _ChromaticBlurStrength * fovealScale, 0.0, _MaxChromaticMip);

                            // Three samples of the same mipped blur RT at per-channel mip levels
                            fixed4 sampleR = tex2Dlod(_BlurTex, float4(texUV, 0, mipR));
                            fixed4 sampleG = tex2Dlod(_BlurTex, float4(texUV, 0, mipG));
                            fixed4 sampleB = tex2Dlod(_BlurTex, float4(texUV, 0, mipB));

                            blurCol = fixed4(sampleR.r, sampleG.g, sampleB.b, 1.0);
#else
                            float lod = blur * _MaxMip; // Choose mip level by blur amount
                            blurCol = tex2Dlod(_BlurTex, float4(texUV, 0, lod)); // Sample mip
#endif
                            outCol = lerp(sharpCol, blurCol, blur); // Blend
                        }
                    }
                }

                // Debug dot
                if (_DebugGazeDot > 0.5)
                {
                    float2 d = uv - gazeUV;      // Vector from gaze center
                    float r = length(d);         // Radius

                    if (r < _DotRadiusUV)
                        outCol.rgb = float3(1, 0, 0); // Red dot
                }

                return outCol; // Output pixel
            }

            ENDCG
        }
    }
}
