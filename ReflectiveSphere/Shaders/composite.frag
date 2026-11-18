#version 330

// Final pass: downsample the supersampled scene, add bloom, vignette and dithering.

in vec2 fragTexCoord;
in vec4 fragColor;

uniform sampler2D texture0;     // supersampled scene
uniform sampler2D bloomNear;    // tight glow
uniform sampler2D bloomFar;     // wide glow
uniform vec2 outTexel;          // 1 / output size, in texture coordinates
uniform float bloomStrength;
uniform float time;

out vec4 finalColor;

void main()
{
    vec2 uv = fragTexCoord;

    // 4 taps spread over one output pixel = a box filter for the supersampled image.
    vec2 o = outTexel * 0.25;
    vec3 c = 0.25 * (texture(texture0, uv + vec2(-o.x, -o.y)).rgb
                   + texture(texture0, uv + vec2( o.x, -o.y)).rgb
                   + texture(texture0, uv + vec2(-o.x,  o.y)).rgb
                   + texture(texture0, uv + vec2( o.x,  o.y)).rgb);

    // Screen-blend the glow so it brightens without clipping to flat white.
    vec3 bloom = (texture(bloomNear, uv).rgb * 0.9 + texture(bloomFar, uv).rgb * 0.8) * bloomStrength;
    c = 1.0 - (1.0 - c) * (1.0 - clamp(bloom, 0.0, 1.0));

    // Gentle lens vignette.
    vec2 d = uv - 0.5;
    c *= clamp(1.0 - dot(d, d) * 0.85, 0.0, 1.0);

    // Dither (+/- 1 level) to stop banding in the dark gradients.
    float n = fract(sin(dot(uv * 1000.0 + time, vec2(12.9898, 78.233))) * 43758.5453);
    c += (n - 0.5) / 255.0;

    finalColor = vec4(c, 1.0);
}
