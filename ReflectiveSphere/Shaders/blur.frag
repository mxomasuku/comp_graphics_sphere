#version 330

// Bloom step 2: separable 9-tap Gaussian blur, using 5 bilinear samples.

in vec2 fragTexCoord;
in vec4 fragColor;

uniform sampler2D texture0;
uniform vec2 direction;   // one texel along x or y

out vec4 finalColor;

void main()
{
    vec3 c = texture(texture0, fragTexCoord).rgb * 0.2270270270;
    c += texture(texture0, fragTexCoord + direction * 1.3846153846).rgb * 0.3162162162;
    c += texture(texture0, fragTexCoord - direction * 1.3846153846).rgb * 0.3162162162;
    c += texture(texture0, fragTexCoord + direction * 3.2307692308).rgb * 0.0702702703;
    c += texture(texture0, fragTexCoord - direction * 3.2307692308).rgb * 0.0702702703;
    finalColor = vec4(c, 1.0);
}
