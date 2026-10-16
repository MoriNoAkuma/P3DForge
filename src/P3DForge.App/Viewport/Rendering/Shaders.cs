namespace P3DForge.App.Viewport.Rendering;

/// <summary>
/// GLSL sources of the renderer. Every face program shares <see cref="FaceVs"/> (vertex layout of
/// <see cref="MeshData"/> + the per-vertex state buffer). Matrices are uploaded so that <c>M * v</c> in GLSL equals
/// the row-vector product <c>v * M</c> of System.Numerics. Instance transforms are only applied when
/// <c>uUseModel == 1</c> (uniforms default to 0, so a forgotten model matrix can never collapse the geometry).
/// </summary>
public static class Shaders
{
    const string NL = "\n";

    public const string FaceVs = """
        #version 410 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUV;
        layout(location = 3) in vec2 aState;   // x = flags (1 selected, 2 proxy, 4 hidden), y = weight (-1 = none)
        layout(location = 4) in vec4 aTangent; // xyz + handedness (bitangent = w * cross(n, t) = +v)
        layout(location = 5) in vec2 aUV1;
        uniform mat4 uViewProj;
        uniform int uUseModel;
        uniform mat4 uModel;     // RENDER->RENDER instance transform
        uniform mat4 uNormalMat; // inverse transpose of uModel
        out vec3 vNormal;
        out vec4 vTangent;
        out vec2 vUV;
        out vec2 vUV1;
        out vec3 vWorld;
        out float vWeight;
        flat out int vFlags;
        void main() {
            vec4 w = vec4(aPos, 1.0);
            vec3 n = aNormal;
            vec4 t = aTangent;
            if (uUseModel == 1) {
                w = uModel * w;
                n = mat3(uNormalMat) * n;
                t = vec4(mat3(uModel) * t.xyz, determinant(mat3(uModel)) < 0.0 ? -t.w : t.w);
            }
            vNormal = n;
            vTangent = t;
            vUV = aUV;
            vUV1 = aUV1;
            vWorld = w.xyz;
            vWeight = aState.y;
            vFlags = int(aState.x + 0.5);
            gl_Position = uViewProj * w;
        }
        """;

    /// <summary>Directional shadow lookup (12-tap Poisson PCF on a hardware-compared depth map).</summary>
    const string ShadowFn = """
        uniform int uShadows;
        uniform sampler2DShadow uShadowMap;
        uniform mat4 uLightViewProj;
        uniform float uShadowSoft;   // PCF radius in texels (0 = hard)
        uniform vec2 uShadowTexel;   // 1 / map size
        uniform float uShadowBias;   // normal offset (world units per texel)
        uniform float uShadowStrength;
        const vec2 POISSON[12] = vec2[](
            vec2(-0.326, -0.406), vec2(-0.840, -0.074), vec2(-0.696, 0.457), vec2(-0.203, 0.621),
            vec2(0.962, -0.195), vec2(0.473, -0.480), vec2(0.519, 0.767), vec2(0.185, -0.893),
            vec2(0.507, 0.064), vec2(0.896, 0.412), vec2(-0.322, -0.933), vec2(-0.792, -0.598));
        float shadowAt(vec3 world, vec3 geomNormal, float ndl) {
            if (uShadows == 0) return 1.0;
            vec3 p = world + geomNormal * uShadowBias * (1.0 + 2.0 * (1.0 - clamp(ndl, 0.0, 1.0)));
            vec4 lp = uLightViewProj * vec4(p, 1.0);
            vec3 c = lp.xyz / lp.w * 0.5 + 0.5;
            if (c.x <= 0.0 || c.x >= 1.0 || c.y <= 0.0 || c.y >= 1.0 || c.z >= 1.0) return 1.0;
            float s;
            if (uShadowSoft <= 0.0) s = texture(uShadowMap, c);
            else {
                s = 0.0;
                for (int i = 0; i < 12; i++) s += texture(uShadowMap, vec3(c.xy + POISSON[i] * uShadowTexel * uShadowSoft, c.z));
                s /= 12.0;
            }
            return mix(1.0, s, uShadowStrength);
        }
        """;

    /// <summary>Lighting rig shared by the lit programs (RENDER-space direction towards the sun).</summary>
    const string LightUniforms = """
        uniform vec3 uSunDir;
        uniform vec3 uSunColor;   // colour x intensity
        uniform vec3 uSkyColor;   // hemispheric ambient, upper half
        uniform vec3 uGroundColor;
        uniform float uFill;      // camera fill light (editing readability)
        uniform float uSpecular;  // specular strength of the editing modes
        """;

    /// <summary>Editing modes (solid / textured): display-space lighting, selection / proxy / hidden / weight tints.</summary>
    public const string FaceFs = """
        #version 410 core
        in vec3 vNormal;
        in vec4 vTangent;
        in vec2 vUV;
        in vec2 vUV1;
        in vec3 vWorld;
        in float vWeight;
        flat in int vFlags;
        uniform vec3 uEye;
        uniform float uBackTint;
        uniform int uUseTex;
        uniform sampler2D uTex;
        uniform float uAlphaTest;
        uniform int uSrgb;
        uniform vec4 uBaseColor;
        uniform float uSelTint;
        uniform int uWeights;
        uniform int uShowProxies;
        uniform float uOpacity;
        uniform float uAlphaMul;   // > 0: blended texture (glass): alpha = texture alpha x this
        """ + NL + LightUniforms + NL + ShadowFn + NL + """
        out vec4 frag;
        vec3 heat(float w) {
            w = clamp(w, 0.0, 1.0);
            vec3 c0 = vec3(0.10, 0.20, 0.85), c1 = vec3(0.10, 0.85, 0.30), c2 = vec3(0.95, 0.90, 0.15), c3 = vec3(0.95, 0.20, 0.10);
            return w < 0.5 ? mix(c0, c1, w * 2.0) : (w < 0.75 ? mix(c1, c2, (w - 0.5) * 4.0) : mix(c2, c3, (w - 0.75) * 4.0));
        }
        void main() {
            vec3 base = uBaseColor.rgb;
            float texAlpha = 1.0;
            if (uUseTex == 1) {
                vec4 t = texture(uTex, vUV);
                if (uAlphaTest > 0.0 && t.a < uAlphaTest) discard;
                texAlpha = t.a;
                // sRGB textures are sampled linear: back to display space, lighting below is "gamma space" like solid mode
                base = uSrgb == 1 ? pow(t.rgb, vec3(1.0 / 2.2)) : t.rgb;
            }
            if (uShowProxies == 1 && (vFlags & 2) != 0) base = mix(base, vec3(0.25, 0.80, 0.95), uUseTex == 1 ? 0.55 : 0.85);
            if (uWeights == 1) base = vWeight < 0.0 ? base * 0.30 + vec3(0.04, 0.05, 0.20) : heat(vWeight);
            vec3 gn = normalize(vNormal);
            if (!gl_FrontFacing) gn = -gn;
            vec3 n = gn;
            vec3 v = normalize(uEye - vWorld);
            float ndl = max(dot(n, uSunDir), 0.0);
            float sh = ndl > 0.0 ? shadowAt(vWorld, gn, ndl) : 1.0;
            vec3 amb = mix(uGroundColor, uSkyColor, 0.5 + 0.5 * n.y);
            vec3 h = normalize(uSunDir + v);
            float spec = ndl > 0.0 ? pow(max(dot(n, h), 0.0), 40.0) * uSpecular * min(1.0, ndl * 4.0) : 0.0;
            vec3 c = base * (amb + uSunColor * ndl * sh + uFill * abs(dot(n, v))) + uSunColor * spec * sh;
            if ((vFlags & 1) != 0) c = mix(c, vec3(1.0, 0.50, 0.18), uSelTint);
            if ((vFlags & 4) != 0) c = mix(c, vec3(0.55, 0.45, 0.75), 0.6);
            if (!gl_FrontFacing) c = mix(c, vec3(0.85, 0.18, 0.18), uBackTint);
            float alpha = (vFlags & 4) != 0 ? min(uOpacity, 0.35) : uOpacity;
            if (uAlphaMul > 0.0) alpha *= texAlpha * uAlphaMul;
            frag = vec4(c, alpha);
        }
        """;

    /// <summary>
    /// "Final" shading from the face material (rvmat Super-shader stages) in linear HDR, tone mapped per fragment.
    /// Stage slots (texture unit = slot): 0 _co, 1 _nohq, 2 _dt, 3 _mc, 4 _as, 5 _smdi, 6 _mask; unit 7 = shadow map.
    /// </summary>
    public const string FinalFs = """
        #version 410 core
        in vec3 vNormal;
        in vec4 vTangent;
        in vec2 vUV;
        in vec2 vUV1;
        in vec3 vWorld;
        in float vWeight;
        flat in int vFlags;
        uniform vec3 uEye;
        uniform sampler2D uTexCo;
        uniform sampler2D uTexNo;
        uniform sampler2D uTexDt;
        uniform sampler2D uTexMc;
        uniform sampler2D uTexAs;
        uniform sampler2D uTexSm;
        uniform sampler2D uTexMask;
        uniform int uHas;          // bit per slot: 1 co, 2 no, 4 dt, 8 mc, 16 as, 32 smdi, 64 mask
        uniform vec3 uUvU[7];      // per slot: u' = dot(uUvU[s], (u, v, 1))
        uniform vec3 uUvV[7];
        uniform int uUvSrc[7];     // 0 = UV set 0 ("tex"), 1 = UV set 1 ("tex1")
        uniform vec3 uAmbient;
        uniform vec3 uDiffuse;
        uniform vec3 uForced;
        uniform vec3 uEmissive;
        uniform vec3 uSpecColor;
        uniform float uSpecPower;
        uniform float uF0;         // fresnel reflectance at normal incidence (< 0: no fresnel stage)
        uniform float uEnv;        // environment reflection amount (0 = no env stage)
        uniform float uAlphaTest;  // > 0: discard below
        uniform float uAlphaMul;   // > 0: blended (alpha = texture alpha x this), 0 = opaque
        uniform int uToggles;      // 1 diffuse, 2 specular, 4 ambient, 8 env, 16 emissive, 32 shadows
        uniform int uStage;        // 0 lit; 1 co, 2 nohq, 3 mc, 4 as, 5 dt, 6 smdi, 7 mask, 8 fresnel, 9 env
        uniform float uExposure;
        uniform int uFlipGreen;
        uniform float uSelTint;
        uniform int uHideProxyFaces;
        uniform float uBackTint;
        """ + NL + LightUniforms + NL + ShadowFn + NL + """
        out vec4 frag;
        vec2 stageUV(int s) {
            vec3 uv = vec3(uUvSrc[s] == 1 ? vUV1 : vUV, 1.0);
            return vec2(dot(uUvU[s], uv), dot(uUvV[s], uv));
        }
        vec3 skyEnv(vec3 d) {
            float y = d.y;
            vec3 zenith = uSkyColor * 1.15;
            vec3 horizon = mix(uSkyColor, vec3(dot(uSkyColor, vec3(0.333))) * 1.6, 0.55);
            vec3 c = y >= 0.0 ? mix(horizon, zenith, pow(clamp(y, 0.0, 1.0), 0.55)) : mix(horizon * 0.55, uGroundColor * 0.8, pow(clamp(-y, 0.0, 1.0), 0.4));
            float s = max(dot(d, uSunDir), 0.0);
            return c + uSunColor * (pow(s, 900.0) * 6.0 + pow(s, 16.0) * 0.12);
        }
        vec3 aces(vec3 x) {
            x *= 0.6;
            return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
        }
        vec3 display(vec3 linear) { return pow(clamp(linear, 0.0, 1.0), vec3(1.0 / 2.2)); }
        void main() {
            if (uHideProxyFaces == 1 && (vFlags & 2) != 0) discard;
            vec4 co = (uHas & 1) != 0 ? texture(uTexCo, stageUV(0)) : vec4(0.5, 0.5, 0.5, 1.0);
            if (uAlphaTest > 0.0 && co.a < uAlphaTest) discard;
            vec3 gn = normalize(vNormal);
            if (!gl_FrontFacing) gn = -gn;
            vec3 n = gn;
            vec4 nohq = vec4(0.5, 0.5, 1.0, 1.0);
            if ((uHas & 2) != 0) {
                nohq = texture(uTexNo, stageUV(1));
                vec2 xy = nohq.rg * 2.0 - 1.0;
                if (uFlipGreen == 1) xy.y = -xy.y;
                vec3 tn = vec3(xy, sqrt(max(1.0 - dot(xy, xy), 0.0)));
                vec3 t = vTangent.xyz - gn * dot(gn, vTangent.xyz);
                if (dot(t, t) > 1e-12) {
                    t = normalize(t);
                    vec3 b = cross(gn, t) * (vTangent.w < 0.0 ? -1.0 : 1.0); // +v = down the image
                    n = normalize(t * tn.x - b * tn.y + gn * tn.z);           // green = up the image
                }
            }
            vec3 v = normalize(uEye - vWorld);
            vec3 albedo = co.rgb;
            vec4 mc = vec4(0.0);
            if ((uHas & 8) != 0) { mc = texture(uTexMc, stageUV(3)); albedo = mix(albedo, mc.rgb, mc.a); }
            vec3 dt = vec3(0.5);
            if ((uHas & 4) != 0) { dt = texture(uTexDt, stageUV(2)).rgb; albedo *= clamp(dt * 2.0, 0.0, 2.0); }
            vec4 as = (uHas & 16) != 0 ? texture(uTexAs, stageUV(4)) : vec4(1.0);
            vec4 sm = (uHas & 32) != 0 ? texture(uTexSm, stageUV(5)) : vec4(1.0);
            vec4 mask = (uHas & 64) != 0 ? texture(uTexMask, stageUV(6)) : vec4(0.0);
            float nv = max(dot(n, v), 0.0);
            float f0 = uF0 >= 0.0 ? uF0 : 0.04;
            float fres = f0 + (1.0 - f0) * pow(1.0 - nv, 5.0);
            vec3 r = reflect(-v, n);
            if (uStage != 0) {
                vec3 c;
                if (uStage == 1) c = display(co.rgb);
                else if (uStage == 2) c = nohq.rgb;
                else if (uStage == 3) c = mix(vec3(0.0), display(mc.rgb), mc.a) + vec3(0.0, 0.0, 0.0);
                else if (uStage == 4) c = as.rgb;
                else if (uStage == 5) c = dt;
                else if (uStage == 6) c = sm.rgb;
                else if (uStage == 7) c = mask.rgb;
                else if (uStage == 8) c = vec3(uF0 >= 0.0 ? fres : 0.0);
                else c = display(aces(skyEnv(r) * uExposure));
                if ((vFlags & 1) != 0) c = mix(c, vec3(1.0, 0.50, 0.18), uSelTint * 0.6);
                frag = vec4(c, 1.0);
                return;
            }
            float ndl = dot(n, uSunDir);
            float gndl = dot(gn, uSunDir);
            float sh = (uToggles & 32) != 0 && gndl > -0.2 ? shadowAt(vWorld, gn, max(gndl, 0.0)) : 1.0;
            if (gndl <= 0.0) sh = min(sh, clamp(1.0 + gndl * 4.0, 0.0, 1.0));
            float ao = as.g;
            vec3 hemi = mix(uGroundColor, uSkyColor, 0.5 + 0.5 * n.y);
            vec3 light = vec3(0.0);
            if ((uToggles & 4) != 0) light += hemi * uAmbient * ao;
            if ((uToggles & 1) != 0) light += uSunColor * (max(ndl, 0.0) * uDiffuse + uForced * ao) * sh;
            vec3 c = albedo * light;
            float gloss = clamp(sm.b, 0.0, 1.0);
            float power = max(uSpecPower * mix(0.08, 1.0, gloss), 1.0);
            vec3 specCol = uSpecColor * sm.g;
            if ((uToggles & 2) != 0 && ndl > 0.0) {
                vec3 h = normalize(uSunDir + v);
                float norm = (power + 8.0) / 25.0;
                c += specCol * uSunColor * pow(max(dot(n, h), 0.0), power) * norm * min(1.0, ndl * 4.0) * sh;
            }
            if ((uToggles & 8) != 0 && uEnv > 0.0) c += skyEnv(r) * fres * uEnv * sm.g * mix(0.35, 1.0, gloss) * mix(1.0, ao, 0.7); // _smdi G scales the reflection, as in game
            if ((uToggles & 16) != 0) c += albedo * uEmissive;
            c = display(aces(c * uExposure));
            if ((vFlags & 1) != 0) c = mix(c, vec3(1.0, 0.50, 0.18), uSelTint);
            if ((vFlags & 4) != 0) c = mix(c, vec3(0.55, 0.45, 0.75), 0.6);
            if (!gl_FrontFacing) c = mix(c, vec3(0.85, 0.18, 0.18), uBackTint);
            frag = vec4(c, uAlphaMul > 0.0 ? co.a * uAlphaMul : 1.0);
        }
        """;

    /// <summary>Shadow-map depth pass (alpha-tested textures keep their holes).</summary>
    public const string DepthVs = """
        #version 410 core
        layout(location = 0) in vec3 aPos;
        layout(location = 2) in vec2 aUV;
        uniform mat4 uLightViewProj;
        uniform int uUseModel;
        uniform mat4 uModel;
        out vec2 vUV;
        void main() {
            vUV = aUV;
            vec4 w = uUseModel == 1 ? uModel * vec4(aPos, 1.0) : vec4(aPos, 1.0);
            gl_Position = uLightViewProj * w;
        }
        """;

    public const string DepthFs = """
        #version 410 core
        in vec2 vUV;
        uniform sampler2D uTex;
        uniform float uAlphaTest;
        void main() {
            if (uAlphaTest > 0.0 && texture(uTex, vUV).a < uAlphaTest) discard;
        }
        """;

    public const string LineVs = """
        #version 410 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec4 aColor;
        uniform mat4 uViewProj;
        uniform int uUseModel;
        uniform mat4 uModel;
        uniform float uPointSize;
        out vec4 vColor;
        void main() {
            vColor = aColor;
            vec4 w = uUseModel == 1 ? uModel * vec4(aPos, 1.0) : vec4(aPos, 1.0);
            gl_Position = uViewProj * w;
            gl_PointSize = aColor.a < 0.99 ? max(1.0, uPointSize - 1.0) : uPointSize + 1.0;
        }
        """;

    // uWire: 0 = colours as given (unselected alpha x uDim), 1 = wireframe (light unselected edges), 2 = ghost wire
    public const string LineFs = """
        #version 410 core
        in vec4 vColor;
        uniform float uDim;
        uniform int uWire;
        uniform int uOnlySelected; // points: draw only selected ones (alpha 1)
        out vec4 frag;
        void main() {
            vec4 c = vColor;
            if (uOnlySelected == 1 && c.a < 0.99) discard;
            if (uWire == 2) c = vec4(1.00, 0.86, 0.10, 0.55); // background LOD: yellow frame (Object Builder)
            else if (c.a < 0.99) c = uWire == 1 ? vec4(0.80, 0.82, 0.87, 0.85) : vec4(c.rgb, c.a * uDim);
            frag = c;
        }
        """;
}
