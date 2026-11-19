// Shared uniforms and helpers, prepended to sphere.frag, floor.frag and sky.frag.
// The floor is defined here so the mirror ball can reflect exactly the same floor.

// The lamp: a stage spotlight aimed at the ball, plus a soft fill light in every direction.
uniform vec3 lightPos;
uniform vec3 lightColor;
uniform float lightPower;       // fill light intensity (all directions)
uniform float spotPower;        // spotlight intensity inside its cone
uniform vec3 spotDir;           // where the spotlight points (at the ball)
uniform float spotCosInner;     // full brightness inside this cone...
uniform float spotCosOuter;     // ...fading to nothing at this one
uniform float lightRadius;      // size of the lamp's lens; larger = softer shadow edges
uniform vec3 viewPos;
uniform vec3 sphereCenter;
uniform float sphereRadius;
uniform float floorY;
uniform float hazeDensity;      // how much light the air scatters per unit length

// Light reflected by the ball's tiles onto the floor, painted each frame by LightSimulation.
uniform sampler2D texture0;
const float SPOT_MAP_EXTENT = 12.0;
const float SPOT_MAP_SCALE = 4.0;

// ACES filmic tone mapping: bright highlights roll off smoothly instead of clipping.
vec3 tonemap(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

float hash(vec3 p)
{
    p = fract(p * 0.3183099 + 0.1);
    p *= 17.0;
    return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

// Smooth 3D value noise in [0, 1].
float noise(vec3 x)
{
    vec3 i = floor(x);
    vec3 f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(hash(i + vec3(0, 0, 0)), hash(i + vec3(1, 0, 0)), f.x),
                   mix(hash(i + vec3(0, 1, 0)), hash(i + vec3(1, 1, 0)), f.x), f.y),
               mix(mix(hash(i + vec3(0, 0, 1)), hash(i + vec3(1, 0, 1)), f.x),
                   mix(hash(i + vec3(0, 1, 1)), hash(i + vec3(1, 1, 1)), f.x), f.y), f.z);
}

// Fractal noise: several octaves of detail layered together.
float fbm(vec3 p, int octaves)
{
    float sum = 0.0, amp = 0.5;
    for (int i = 0; i < octaves; i++)
    {
        sum += amp * noise(p);
        p = p * 2.03 + vec3(1.7, 9.2, 3.1);
        amp *= 0.5;
    }
    return sum;
}

// Bump mapping without tangents (Mikkelsen 2010): tilt normal N by the slope of a
// height field h (in world units) measured with screen-space derivatives.
vec3 bumpNormal(vec3 N, vec3 P, float h)
{
    vec3 dpdx = dFdx(P), dpdy = dFdy(P);
    float dhdx = dFdx(h), dhdy = dFdy(h);
    vec3 r1 = cross(dpdy, N);
    vec3 r2 = cross(N, dpdx);
    float det = dot(dpdx, r1);
    vec3 grad = sign(det) * (dhdx * r1 + dhdy * r2);
    return normalize(abs(det) * N - grad);
}

// Schlick's Fresnel: surfaces reflect more at glancing angles.
float fresnel(float cosTheta, float f0)
{
    return f0 + (1.0 - f0) * pow(1.0 - clamp(cosTheta, 0.0, 1.0), 5.0);
}

vec3 fresnel(float cosTheta, vec3 f0)
{
    return f0 + (1.0 - f0) * pow(1.0 - clamp(cosTheta, 0.0, 1.0), 5.0);
}

// The lamp's intensity in a direction leaving it: fill everywhere, spotlight in its cone.
float lightIntensity(vec3 dirFromLamp)
{
    return lightPower + spotPower * smoothstep(spotCosOuter, spotCosInner, dot(dirFromLamp, spotDir));
}

// Light arriving at p from the lamp: inverse-square falloff, like a real light.
vec3 lightRadiance(vec3 p)
{
    vec3 d = p - lightPos;
    float d2 = dot(d, d);
    return lightColor * lightIntensity(d * inversesqrt(d2)) / d2;
}

// Irradiance on the floor from beams reflected by the ball's mirror tiles.
vec3 reflectedSpots(vec3 p)
{
    vec2 uv = p.xz / (2.0 * SPOT_MAP_EXTENT) + 0.5;
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) return vec3(0.0);
    return texture(texture0, vec2(uv.x, 1.0 - uv.y)).rgb * SPOT_MAP_SCALE;
}

// Soft shadow of the sphere: how close does the ray from this point to the light pass
// to the sphere's centre? Closer than the radius = blocked. The penumbra widens the
// further the point is from the sphere relative to the light, like a real area light.
float sphereShadow(vec3 p)
{
    vec3 toLight = lightPos - p;
    float segLen = length(toLight);
    vec3 dir = toLight / segLen;
    float t = dot(sphereCenter - p, dir);
    if (t <= 0.0 || t >= segLen) return 1.0;
    float missDist = length(sphereCenter - (p + dir * t));
    float penumbra = 0.04 + lightRadius * t / max(segLen - t, 0.001);
    return smoothstep(sphereRadius - penumbra, sphereRadius + penumbra, missDist);
}

// ---------------------------------------------------------------------------------
// Floor: polished marble tiles (1 x 1 units) separated by grout.

struct FloorMaterial
{
    vec3 albedo;
    float height;   // relief for bump mapping
    float gloss;    // 0 in the grout, ~1 on polished stone
};

FloorMaterial floorMaterial(vec2 xz)
{
    vec2 cell = floor(xz);
    vec2 f = fract(xz);
    float edge = min(min(f.x, 1.0 - f.x), min(f.y, 1.0 - f.y));
    const float groutWidth = 0.02;
    float grout = 1.0 - smoothstep(groutWidth * 0.6, groutWidth, edge);
    float bevel = smoothstep(groutWidth, groutWidth * 3.5, edge);   // rounded tile edges

    float tileRand = hash(vec3(cell, 7.0));
    float n = fbm(vec3(xz * 1.2, tileRand * 13.0), 4);
    // Marble veins: thin bright streaks along a warped sine.
    float veins = pow(1.0 - abs(sin((xz.x * 1.3 + xz.y * 0.7 + n * 5.0) * 2.2)), 18.0);
    vec3 stone = mix(vec3(0.075, 0.078, 0.088), vec3(0.16, 0.16, 0.17), n);
    stone *= 0.8 + 0.4 * tileRand;
    stone += veins * vec3(0.055, 0.055, 0.058) * (0.5 + n);

    FloorMaterial m;
    m.albedo = mix(stone, vec3(0.02, 0.02, 0.022), grout);
    // Tiles sit slightly proud of the grout, with a faint ripple in the polish.
    m.height = 0.006 * bevel + 0.0004 * noise(vec3(xz * 30.0, 2.0));
    m.gloss = (1.0 - grout) * clamp(0.75 + 0.5 * n, 0.0, 1.0);
    return m;
}

// Direct lighting on the floor (linear colour, before tone mapping).
vec3 shadeFloor(vec3 P, vec3 N, vec3 V, FloorMaterial m)
{
    vec3 L = normalize(lightPos - P);
    vec3 radiance = lightRadiance(P);
    float NdotL = max(dot(N, L), 0.0);
    float shadow = sphereShadow(P);

    // Contact darkening where the sphere is close to the floor (ambient occlusion).
    vec3 d = P - sphereCenter;
    float ao = 1.0 - 0.7 * clamp(sphereRadius * sphereRadius / dot(d, d), 0.0, 1.0);

    // A broad sheen; the sharp mirror image of the bulb comes from the reflection pass.
    vec3 H = normalize(L + V);
    float sheen = 0.4 * pow(max(dot(N, H), 0.0), 40.0) * m.gloss;

    vec3 ambient = vec3(0.012, 0.013, 0.016);
    return m.albedo * (ambient * ao + NdotL * radiance * shadow + reflectedSpots(P))
         + sheen * radiance * shadow;
}

// The floor fades into the dark studio towards its far edge.
float floorFade(vec3 P)
{
    return smoothstep(8.0, 22.0, length(P.xz));
}

// The dark studio: a soft bright band at the horizon (like a cyclorama wall catching
// the light) fading to near black overhead. This is what makes chrome read as chrome.
vec3 skyColor(vec3 d)
{
    float band = exp(-abs(d.y) * 7.0);
    return mix(vec3(0.004, 0.005, 0.008), vec3(0.20, 0.20, 0.23), band);
}

// What a mirror ray from ro in direction rd sees: the bulb, the lit floor, or the studio.
vec3 traceEnvironment(vec3 ro, vec3 rd)
{
    // Bulb: ray-sphere intersection.
    vec3 oc = ro - lightPos;
    float b = dot(oc, rd);
    float c = dot(oc, oc) - lightRadius * lightRadius;
    float disc = b * b - c;
    float tBulb = disc > 0.0 ? -b - sqrt(disc) : -1.0;

    // Floor: ray-plane intersection.
    if (rd.y < -1e-4)
    {
        float t = (floorY - ro.y) / rd.y;
        if (t > 0.0 && (tBulb < 0.0 || t < tBulb))
        {
            vec3 hit = ro + rd * t;
            FloorMaterial m = floorMaterial(hit.xz);
            vec3 floorColor = shadeFloor(hit, vec3(0.0, 1.0, 0.0), -rd, m);
            return mix(floorColor, skyColor(rd), floorFade(hit));
        }
    }

    // The lamp's lens: a disc of radius lightRadius emitting the lamp's intensity.
    if (tBulb > 0.0)
        return lightColor * lightIntensity(normalize(ro - lightPos)) / (3.14159 * lightRadius * lightRadius);

    // The glowing haze: faint everywhere, bright looking back up the spotlight's beam.
    float towardsLamp = max(dot(rd, normalize(lightPos - ro)), 0.0);
    vec3 hazeGlow = hazeDensity * lightColor * (lightPower * 0.02 + spotPower * 0.003 * pow(towardsLamp, 10.0));
    return skyColor(rd) + hazeGlow;
}

// ---------------------------------------------------------------------------------
// Haze: light scattered towards the eye by particles in the air.

// Henyey-Greenstein phase function: g > 0 scatters mostly forwards (like smoke or dust).
float hgPhase(float cosTheta, float g)
{
    float g2 = g * g;
    return (1.0 - g2) / (12.566 * pow(1.0 + g2 - 2.0 * g * cosTheta, 1.5));
}

// Light from the lamp scattered along the ray segment [t0, t1]. Equiangular sampling
// (Kulla & Fajardo 2012) places samples where the lamp is closest, which cancels the
// 1/d^2 falloff and keeps the glow smooth. The ball's shadow is tested at every sample.
vec3 scatterSegment(vec3 ro, vec3 rd, float t0, float t1, bool spot, float jitter)
{
    float delta = dot(lightPos - ro, rd);
    float D = max(length(ro + rd * delta - lightPos), 1e-3);
    float a = atan(t0 - delta, D), b = atan(t1 - delta, D);
    const int N = 12;
    float sum = 0.0;
    for (int i = 0; i < N; i++)
    {
        float t = delta + D * tan(mix(a, b, (float(i) + jitter) / float(N)));
        vec3 p = ro + rd * t;
        vec3 fromLamp = normalize(p - lightPos);
        float intensity = spot
            ? spotPower * smoothstep(spotCosOuter, spotCosInner, dot(fromLamp, spotDir))
            : lightPower;
        sum += intensity * hgPhase(dot(fromLamp, -rd), 0.35) * sphereShadow(p) * exp(-hazeDensity * t);
    }
    return lightColor * hazeDensity * (b - a) / D * sum / float(N);
}

// The part of a ray (from 0 to tMax) inside the spotlight's cone, if any.
bool coneInterval(vec3 ro, vec3 rd, float tMax, out float tA, out float tB)
{
    vec3 co = ro - lightPos;
    float cos2 = spotCosOuter * spotCosOuter;
    float dv = dot(rd, spotDir), cv = dot(co, spotDir);
    float a = dv * dv - cos2;
    float b = 2.0 * (dv * cv - dot(rd, co) * cos2);
    float c = cv * cv - dot(co, co) * cos2;
    tA = 0.0;
    tB = tMax;

    // Only the half of the cone in front of the lamp.
    if (abs(dv) > 1e-6)
    {
        float tp = -cv / dv;
        if (dv > 0.0) tA = max(tA, tp); else tB = min(tB, tp);
    }
    else if (cv < 0.0) return false;

    float disc = b * b - 4.0 * a * c;
    if (abs(a) < 1e-6)
    {
        if (abs(b) > 1e-9) { float t = -c / b; if (b > 0.0) tA = max(tA, t); else tB = min(tB, t); }
        else if (c < 0.0) return false;
    }
    else if (disc < 0.0)
    {
        if (a < 0.0) return false; // never inside
    }
    else
    {
        float s = sqrt(disc);
        float r1 = (-b - s) / (2.0 * a), r2 = (-b + s) / (2.0 * a);
        if (r1 > r2) { float tmp = r1; r1 = r2; r2 = tmp; }
        if (a < 0.0) { tA = max(tA, r1); tB = min(tB, r2); }
        else if (tA < r1) tB = min(tB, r1);
        else tA = max(tA, r2);
    }
    return tB > tA;
}

// All the haze glow along a camera ray: the faint fill everywhere plus the spotlight's
// bright cone (with the ball's shadow running through it).
vec3 hazeInScatter(vec3 ro, vec3 rd, float tMax)
{
    if (hazeDensity <= 0.0) return vec3(0.0);
    // Interleaved gradient noise: a different sample offset per pixel, so the few samples
    // average out across neighbouring pixels instead of banding.
    float jitter = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
    vec3 glow = scatterSegment(ro, rd, 0.0, tMax, false, jitter);
    float tA, tB;
    if (spotPower > 0.0 && coneInterval(ro, rd, tMax, tA, tB))
        glow += scatterSegment(ro, rd, tA, tB, true, jitter);
    return glow;
}

// Fraction of light that survives travelling a distance t through the haze.
float hazeTransmittance(float t)
{
    return exp(-hazeDensity * t);
}
