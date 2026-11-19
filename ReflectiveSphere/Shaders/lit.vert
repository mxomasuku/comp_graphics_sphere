#version 330

// Attribute and matrix names are the ones raylib binds automatically.
in vec3 vertexPosition;
in vec3 vertexNormal;
in vec2 vertexTexCoord;
in vec4 vertexColor;      // tile colour on the mirror ball (white elsewhere)

uniform mat4 mvp;        // projection * view * model
uniform mat4 matModel;   // object -> world (includes the sphere's spin and position)
uniform mat4 matNormal;  // transpose(inverse(matModel)), keeps normals perpendicular

out vec3 fragPosition;   // world space
out vec3 fragNormal;     // world space
out vec2 fragTexCoord;
out vec3 fragObjPos;     // object space: surface detail sticks to the spinning sphere
out vec4 fragColor;

void main()
{
    fragPosition = vec3(matModel * vec4(vertexPosition, 1.0));
    fragNormal = normalize(vec3(matNormal * vec4(vertexNormal, 0.0)));
    fragTexCoord = vertexTexCoord;
    fragObjPos = vertexPosition;
    fragColor = vertexColor;
    gl_Position = mvp * vec4(vertexPosition, 1.0);
}
