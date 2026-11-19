in vec3 fragPosition;
in vec3 fragNormal;
in vec2 fragTexCoord;
in vec3 fragObjPos;
in vec4 fragColor;

out vec4 finalColor;

void main()
{
    vec3 P = fragPosition;
    FloorMaterial m = floorMaterial(P.xz);
    vec3 N = bumpNormal(vec3(0.0, 1.0, 0.0), P, m.height);
    vec3 V = normalize(viewPos - P);
    float dist = length(viewPos - P);

    // Lamp light, the ball's shadow, and the spots of light reflected by the ball's tiles,
    // dimmed by the haze in between (the haze's own glow is added by haze.frag).
    vec3 color = shadeFloor(P, N, V, m) * hazeTransmittance(dist);
    color = pow(tonemap(color), vec3(1.0 / 2.2));

    // Alpha lets the mirrored scene (drawn first, beneath the floor) show through:
    // more reflective at glancing angles, never in the grout, fading at the far edge.
    float F = fresnel(dot(N, V), 0.06);
    float reflectivity = clamp(0.22 + 0.6 * F, 0.0, 0.8) * m.gloss;
    finalColor = vec4(color, (1.0 - reflectivity) * (1.0 - floorFade(P)));
}
