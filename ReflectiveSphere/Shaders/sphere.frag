in vec3 fragPosition;
in vec3 fragNormal;
in vec2 fragTexCoord;
in vec3 fragObjPos;

uniform float shininess;        // Phong exponent: higher = tighter, sharper highlight
uniform float specularStrength;
uniform float mirrored;         // 1 while drawing the sphere's reflection in the floor

out vec4 finalColor;

void main()
{
    vec3 P = fragPosition;
    vec3 N = normalize(fragNormal);

    // --- Tiles: a 16 x 8 checker, measured in object units so the seams keep an even
    // width all the way to the poles. dP/du and dP/dv come from screen-space derivatives.
    const vec2 tiles = vec2(16.0, 8.0);
    vec2 uv = fragTexCoord;
    vec2 t = uv * tiles;
    vec2 cell = floor(t);
    vec2 f = fract(t);

    vec3 dp1 = dFdx(fragObjPos), dp2 = dFdy(fragObjPos);
    vec2 duv1 = dFdx(uv), duv2 = dFdy(uv);
    float det = duv1.x * duv2.y - duv1.y * duv2.x;
    float lenU = 1.0, lenV = 1.0;
    if (abs(det) > 1e-12)
    {
        lenU = length((dp1 * duv2.y - dp2 * duv1.y) / det) / tiles.x;
        lenV = length((dp2 * duv1.x - dp1 * duv2.x) / det) / tiles.y;
    }
    float edgeDist = min(min(f.x, 1.0 - f.x) * lenU, min(f.y, 1.0 - f.y) * lenV);
    const float grooveWidth = 0.03;                        // object units (sphere radius = 1)
    float groove = 1.0 - smoothstep(0.0, grooveWidth, edgeDist);

    // Rounded seams between the tiles, and a very faint ripple in the polish.
    float h = -0.015 * groove * groove + 0.0003 * noise(fragObjPos * 40.0);
    vec3 Nb = bumpNormal(N, P, h);

    // In the reflection pass the sphere is drawn mirrored below the floor. Shade it as
    // the real sphere seen from a mirrored camera, which is exactly what a mirror shows.
    vec3 eye = viewPos;
    if (mirrored > 0.5)
    {
        P.y = 2.0 * floorY - P.y;
        N.y = -N.y;
        Nb.y = -Nb.y;
        eye.y = 2.0 * floorY - eye.y;
    }

    vec3 V = normalize(eye - P);
    vec3 L = normalize(lightPos - P);
    vec3 radiance = lightRadiance(P);
    float NdotL = max(dot(Nb, L), 0.0);
    float NdotV = max(dot(Nb, V), 0.0);

    // --- Polished metal: alternating silver and gold chrome tiles.
    float checker = mod(cell.x + cell.y, 2.0);
    vec3 F0 = mix(vec3(0.95, 0.95, 0.97), vec3(1.0, 0.74, 0.34), checker);
    F0 *= 0.95 + 0.05 * fbm(fragObjPos * 5.0, 3);
    vec3 F = fresnel(NdotV, F0);

    // Mirror reflection of the surroundings. Law of reflection: the view ray is mirrored
    // about the normal, so it leaves at the same angle it arrived.
    vec3 Rv = reflect(-V, Nb);
    vec3 envColor = traceEnvironment(P + N * 0.01, Rv);

    // The bulb's highlight: light mirrored about the normal (angle in = angle out) is
    // brightest where it heads straight for the eye. Shininess sets how tight it is.
    vec3 R = reflect(-L, Nb);
    float spec = NdotL > 0.0 ? (shininess + 2.0) / 8.0 * pow(max(dot(R, V), 0.0), shininess) : 0.0;

    vec3 metal = F * envColor + F0 * specularStrength * spec * radiance;

    // The seams are dark, rough enamel and barely reflect.
    vec3 seam = vec3(0.012) * (vec3(0.04) + NdotL * radiance);
    vec3 color = mix(metal, seam, groove);

    // The floor reflection fades the further the reflected point is below the floor.
    if (mirrored > 0.5) color *= exp(-(floorY - fragPosition.y) * 0.8);

    finalColor = vec4(pow(tonemap(color), vec3(1.0 / 2.2)), 1.0);
}
