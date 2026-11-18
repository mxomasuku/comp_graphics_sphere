#version 330

// Bloom step 1: downsample (4-tap box) and keep only the bright parts of the image.

in vec2 fragTexCoord;
in vec4 fragColor;

uniform sampler2D texture0;
uniform vec2 texelSize;   // 1 / source size
uniform float threshold;  // 0 = keep everything (plain downsample)

out vec4 finalColor;

void main()
{
    vec3 c = 0.25 * (texture(texture0, fragTexCoord + texelSize * vec2(-1.0, -1.0)).rgb
                   + texture(texture0, fragTexCoord + texelSize * vec2( 1.0, -1.0)).rgb
                   + texture(texture0, fragTexCoord + texelSize * vec2(-1.0,  1.0)).rgb
                   + texture(texture0, fragTexCoord + texelSize * vec2( 1.0,  1.0)).rgb);

    // Soft knee so the cut-off doesn't create hard edges.
    float brightness = max(c.r, max(c.g, c.b));
    const float knee = 0.2;
    float soft = clamp(brightness - threshold + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee + 1e-4);
    float contribution = max(soft, brightness - threshold) / max(brightness, 1e-4);

    finalColor = vec4(c * clamp(contribution, 0.0, 1.0), 1.0);
}
