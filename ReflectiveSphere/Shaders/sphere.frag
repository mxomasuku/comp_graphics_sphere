in vec3 fragPosition;
in vec3 fragNormal;
in vec2 fragTexCoord;
in vec3 fragObjPos;
in vec4 fragColor;              // the tile's mirror colour: silver or gold

uniform float shininess;        // Phong exponent: higher = tighter, sharper glints
uniform float specularStrength;
uniform float mirrored;         // 1 while drawing the ball's reflection in the floor

out vec4 finalColor;

void main()
{
    vec3 P = fragPosition;
    vec3 N = normalize(fragNormal);  // each tile is flat: one normal for the whole tile

    // The tile's edges are slightly rounded, so they catch the light like real mirror tiles.
    vec2 uv = fragTexCoord;
    float edge = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
    float h = 0.004 * smoothstep(0.0, 0.14, edge);
    vec3 Nb = bumpNormal(N, P, h);

    // In the reflection pass the ball is drawn mirrored below the floor. Shade it as the
    // real ball seen from a mirrored camera, which is exactly what a mirror shows.
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

    vec3 F0 = fragColor.rgb;
    vec3 F = fresnel(NdotV, F0);

    // Mirror reflection of the surroundings (lamp, floor with its light spots, studio):
    // the view ray is mirrored about the tile, leaving at the angle it arrived.
    vec3 Rv = reflect(-V, Nb);
    vec3 envColor = traceEnvironment(P + N * 0.01, Rv);

    // Glint: the lamp's light mirrored about the tile is brightest where it heads for the eye.
    vec3 R = reflect(-L, Nb);
    float spec = NdotL > 0.0 ? (shininess + 2.0) / 8.0 * pow(max(dot(R, V), 0.0), shininess) : 0.0;

    vec3 color = F * envColor + F0 * specularStrength * spec * radiance;

    if (mirrored > 0.5)
    {
        // The floor reflection fades the further the reflected point is below the floor.
        color *= exp(-(floorY - fragPosition.y) * 0.8);
    }
    else
    {
        // Dimmed by the haze between the ball and the camera (its glow comes from haze.frag).
        color *= hazeTransmittance(length(viewPos - P));
    }

    finalColor = vec4(pow(tonemap(color), vec3(1.0 / 2.2)), 1.0);
}
