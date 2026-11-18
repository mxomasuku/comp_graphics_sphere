in vec3 fragPosition;
in vec3 fragNormal;
in vec2 fragTexCoord;
in vec3 fragObjPos;

out vec4 finalColor;

void main()
{
    vec3 P = fragPosition;
    FloorMaterial m = floorMaterial(P.xz);
    vec3 N = bumpNormal(vec3(0.0, 1.0, 0.0), P, m.height);
    vec3 V = normalize(viewPos - P);

    vec3 color = pow(tonemap(shadeFloor(P, N, V, m)), vec3(1.0 / 2.2));

    // Alpha lets the mirrored scene (drawn first, beneath the floor) show through:
    // more reflective at glancing angles, never in the grout, fading at the far edge.
    float F = fresnel(dot(N, V), 0.06);
    float reflectivity = clamp(0.22 + 0.6 * F, 0.0, 0.8) * m.gloss;
    finalColor = vec4(color, (1.0 - reflectivity) * (1.0 - floorFade(P)));
}
