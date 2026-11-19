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

void main()
{
    vec2 ndc = gl_FragCoord.xy / resolution * 2.0 - 1.0;
    vec3 rd = normalize(camForward + ndc.x * aspect * tanHalfFov * camRight + ndc.y * tanHalfFov * camUp);

    // A very dark studio behind everything, seen through the haze (whose glow is added
    // separately by haze.frag).
    vec3 backdrop = mix(vec3(0.010, 0.011, 0.016), vec3(0.002, 0.0025, 0.004), smoothstep(-0.1, 0.6, rd.y));
    vec3 color = backdrop * hazeTransmittance(40.0);
    finalColor = vec4(pow(tonemap(color), vec3(1.0 / 2.2)), 1.0);
}
