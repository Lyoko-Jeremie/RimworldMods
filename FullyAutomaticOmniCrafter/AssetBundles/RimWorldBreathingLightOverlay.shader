Shader "Custom/RimWorldBreathingLightOverlay" {
    Properties {
        _MainTex ("Light Overlay (Transparent Background)", 2D) = "white" {}
        _Color ("Light Tint Color", Color) = (1,1,1,1)
        _Speed ("Breathing Speed", Float) = 3.0
        _MinAlpha ("Minimum Brightness", Range(0, 1)) = 0.2 // 呼吸到最暗时的透明度
        _MaxAlpha ("Maximum Brightness", Range(0, 1)) = 1.0 // 呼吸到最亮时的透明度
    }
    SubShader {
        // 关键标签：告诉引擎这是透明物体，不要写入深度缓冲
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        ZWrite Off

        // 【混合模式选择】
        // 模式 A (标准透明): 适合边缘轮廓清晰的灯贴图
        Blend SrcAlpha OneMinusSrcAlpha 
        
        // 模式 B (叠加发光/Additive): 极其适合光晕、霓虹灯！它会让灯光部分像屏幕发光一样叠加在底图上。
        // 如果你觉得灯不够亮，把上面那行删掉，换成下面这行：
        // Blend SrcAlpha One
        
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            // ==========================================
            // 【核心修复区域】
            // 排除手机端 (gles, gles3) 和老旧 DX9 显卡
            // 解决 GitHub Actions (Linux) 编译报错问题
            #pragma exclude_renderers gles gles3 d3d11_9x
            // 明确指定编译目标为 PC 级别的 Shader 模型 3.0
            #pragma target 3.0
            // ==========================================
            
            #include "UnityCG.cginc"

            struct appdata_t {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f {
                float4 vertex : SV_POSITION;
                float2 texcoord : TEXCOORD0;
            };

            sampler2D _MainTex;
            float4 _Color;
            float _Speed;
            float _MinAlpha;
            float _MaxAlpha;

            v2f vert (appdata_t v) {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = v.texcoord;
                return o;
            }

            // 使用 float4 替代 fixed4，更符合 PC 端标准
            float4 frag (v2f i) : SV_Target {
                // 读取你的"透明底+灯条"贴图的原始颜色和透明度
                float4 texColor = tex2D(_MainTex, i.texcoord);

                // 计算呼吸因子：利用 _Time.y 和 sin 函数，生成一个 0 到 1 之间丝滑循环的值
                float breathFactor = (sin(_Time.y * _Speed) + 1.0) * 0.5;
                // 利用 lerp 函数，将 0~1 的呼吸因子映射到你设定的 _MinAlpha 和 _MaxAlpha 之间
                float currentAlpha = lerp(_MinAlpha, _MaxAlpha, breathFactor);

                // 叠加颜色，并将最终算出的动态透明度乘上贴图原本的透明度（确保透明背景依然是透明的）
                float4 finalColor = texColor * _Color;
                finalColor.a *= currentAlpha;

                return finalColor;
            }
            ENDCG
        }
    }
}
