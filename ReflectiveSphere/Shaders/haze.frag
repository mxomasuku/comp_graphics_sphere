in vec2 fragTexCoord;
in vec4 fragColor;

// Camera, to turn each pixel into a view ray.
uniform vec2 resolution;
uniform vec3 camForward;
uniform vec3 camRight;
uniform vec3 camUp;
uniform float tanHalfFov;
uniform float aspect;

out vec4 finalColor;

// Light scattered by the haze along each view ray, rendered at reduced resolution (haze
// is smooth) and blended over the finished image. The ray stops where it meets the ball
// or the floor, found analytically, so no depth buffer is needed.
void main()
{
    vec2 ndc = gl_FragCoord.xy / resolution * 2.0 - 1.0;
    vec3 rd = normalize(camForward + ndc.x * aspect * tanHalfFov * camRight + ndc.y * tanHalfFov * camUp);

    float tMax = 40.0;
    if (rd.y < -1e-4) tMax = min(tMax, (floorY - viewPos.y) / rd.y);

    vec3 oc = viewPos - sphereCenter;
    float b = dot(oc, rd);
    float disc = b * b - (dot(oc, oc) - sphereRadius * sphereRadius);
    if (disc > 0.0)
    {
        float t = -b - sqrt(disc);
        if (t > 0.0) tMax = min(tMax, t);
    }

    // The lamp's housing (roughly a ball just behind the lens) hides the glow behind it.
    vec3 oh = viewPos - (lightPos - spotDir * 0.3);
    float bh = dot(oh, rd);
    float dh = bh * bh - (dot(oh, oh) - 0.27 * 0.27);
    if (dh > 0.0)
    {
        float t = -bh - sqrt(dh);
        if (t > 0.0) tMax = min(tMax, t);
    }

    vec3 glow = hazeInScatter(viewPos, rd, tMax);
    finalColor = vec4(pow(tonemap(glow), vec3(1.0 / 2.2)), 1.0);
}
