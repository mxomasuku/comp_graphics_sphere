using System.Numerics;
using Raylib_cs;

namespace ReflectiveSphere;

/// <summary>
/// A shiny, patterned sphere that bounces around a box in x, y and z, spins about its
/// own axis, and reflects a glowing point light whose angle can be changed at runtime.
/// </summary>
public sealed class SphereScene : IDisposable
{
    // Scene
    private static readonly Vector3 BoxHalfExtents = new(4.0f, 2.5f, 3.0f);
    private static readonly float FloorY = -BoxHalfExtents.Y;
    private const float SphereRadius = 0.9f;

    // Translation: moves in all three dimensions and bounces off the box walls
    private Vector3 _position;
    private Vector3 _velocity;

    // Self-rotation (spin) about a tilted axis through the sphere's centre
    private static readonly Vector3 SpinAxis = Vector3.Normalize(new Vector3(0.3f, 1.0f, 0.2f));
    private float _spinAngleDeg;
    private float _spinSpeedDeg = 90f; // degrees / second
    private bool _paused;

    // Point light (a bulb), positioned by two angles and a distance from the centre
    private float _lightAzimuthDeg = 45f;   // around the vertical (Y) axis
    private float _lightElevationDeg = 35f; // above the horizon
    private float _lightDistance = 6.5f;
    private const float LightRadius = 0.16f;
    private static readonly Vector3 LightColor = new(1.0f, 0.86f, 0.68f); // warm, like a tungsten bulb
    private const float LightPower = 70f;
    private float _shininess = 220f;

    // Orbit camera
    private float _camYawDeg = 90f, _camPitchDeg = 15f, _camDistance = 13f;

    private readonly PostProcess _post;
    private readonly Hud _hud;
    private readonly ReflectionGuide _guide = new();
    private readonly Shader _sphereShader, _floorShader;
    private Model _sphere, _floor, _bulb;
    private readonly Texture2D _glow;

    public SphereScene()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        _post = new PostProcess(dir);
        _hud = new Hud();

        // The two surface shaders share helper functions from common.glsl.
        string vert = File.ReadAllText(Path.Combine(dir, "lit.vert"));
        string common = "#version 330\n" + File.ReadAllText(Path.Combine(dir, "common.glsl")) + "\n";
        _sphereShader = Raylib.LoadShaderFromMemory(vert, common + File.ReadAllText(Path.Combine(dir, "sphere.frag")));
        _floorShader = Raylib.LoadShaderFromMemory(vert, common + File.ReadAllText(Path.Combine(dir, "floor.frag")));

        SetUniform(_sphereShader, "specularStrength", 1.0f);
        foreach (var shader in new[] { _sphereShader, _floorShader })
        {
            SetUniform(shader, "lightColor", LightColor);
            SetUniform(shader, "lightPower", LightPower);
            SetUniform(shader, "lightRadius", LightRadius);
            SetUniform(shader, "sphereRadius", SphereRadius);
            SetUniform(shader, "floorY", FloorY);
        }

        // Unit sphere; scaled to SphereRadius by its transform.
        _sphere = Raylib.LoadModelFromMesh(Raylib.GenMeshSphere(1f, 96, 192));
        _floor = Raylib.LoadModelFromMesh(Raylib.GenMeshPlane(60f, 60f, 1, 1));
        _bulb = Raylib.LoadModelFromMesh(Raylib.GenMeshSphere(LightRadius, 32, 32));
        unsafe
        {
            _sphere.Materials[0].Shader = _sphereShader;
            _floor.Materials[0].Shader = _floorShader;
        }

        _glow = CreateGlowTexture(256);
        Reset();
    }

    /// <summary>A soft radial falloff used as a camera-facing halo around the bulb.</summary>
    private static Texture2D CreateGlowTexture(int size)
    {
        Image image = Raylib.GenImageColor(size, size, Color.Blank);
        float half = size / 2f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float r = MathF.Sqrt((x - half) * (x - half) + (y - half) * (y - half)) / half; // 0..1+
            // Bright core plus a long, faint tail, faded to zero at the edge.
            float intensity = (MathF.Exp(-r * r * 30f) + 0.35f * MathF.Exp(-r * 5f)) * MathF.Max(0f, 1f - r);
            byte a = (byte)Math.Clamp(intensity * 255f, 0f, 255f);
            Raylib.ImageDrawPixel(ref image, x, y, new Color((byte)255, (byte)255, (byte)255, a));
        }
        Texture2D texture = Raylib.LoadTextureFromImage(image);
        Raylib.SetTextureFilter(texture, TextureFilter.Bilinear);
        Raylib.UnloadImage(image);
        return texture;
    }

    private static void SetUniform(Shader shader, string name, float value) =>
        Raylib.SetShaderValue(shader, Raylib.GetShaderLocation(shader, name), value, ShaderUniformDataType.Float);

    private static void SetUniform(Shader shader, string name, Vector3 value) =>
        Raylib.SetShaderValue(shader, Raylib.GetShaderLocation(shader, name), value, ShaderUniformDataType.Vec3);

    /// <summary>How many rays the reflection diagram shows.</summary>
    public RayMode RayMode
    {
        get => _guide.Mode;
        set => _guide.Mode = value;
    }

    private void Reset()
    {
        _position = Vector3.Zero;
        _velocity = new Vector3(2.3f, 1.7f, 1.4f);
        _spinAngleDeg = 0f;
    }

    private Vector3 LightPosition
    {
        get
        {
            float az = _lightAzimuthDeg * Raylib.DEG2RAD;
            float el = _lightElevationDeg * Raylib.DEG2RAD;
            return _lightDistance * new Vector3(MathF.Cos(el) * MathF.Sin(az), MathF.Sin(el), MathF.Cos(el) * MathF.Cos(az));
        }
    }

    private Camera3D Camera
    {
        get
        {
            float yaw = _camYawDeg * Raylib.DEG2RAD;
            float pitch = _camPitchDeg * Raylib.DEG2RAD;
            var position = _camDistance * new Vector3(
                MathF.Cos(pitch) * MathF.Cos(yaw),
                MathF.Sin(pitch),
                MathF.Cos(pitch) * MathF.Sin(yaw));
            return new Camera3D(position, Vector3.Zero, Vector3.UnitY, 45f, CameraProjection.Perspective);
        }
    }

    /// <summary>Mirror a point through the floor plane.</summary>
    private static Vector3 Mirror(Vector3 p) => new(p.X, 2f * FloorY - p.Y, p.Z);

    public void Update(float dt)
    {
        HandleInput(dt);
        if (_paused) return;

        // Move in x, y and z; reflect the velocity component that hits a wall.
        _position += _velocity * dt;
        for (int axis = 0; axis < 3; axis++)
        {
            float limit = BoxHalfExtents[axis] - SphereRadius;
            if (_position[axis] > limit) { _position[axis] = limit; _velocity[axis] = -MathF.Abs(_velocity[axis]); }
            if (_position[axis] < -limit) { _position[axis] = -limit; _velocity[axis] = MathF.Abs(_velocity[axis]); }
        }

        // Rotate on itself
        _spinAngleDeg = (_spinAngleDeg + _spinSpeedDeg * dt) % 360f;

        // Light pulses travel along the diagram's rays.
        _guide.Advance(dt);
    }

    private void HandleInput(float dt)
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Space)) _paused = !_paused;
        if (Raylib.IsKeyPressed(KeyboardKey.R)) Reset();
        if (Raylib.IsKeyPressed(KeyboardKey.B)) _post.BloomEnabled = !_post.BloomEnabled;
        if (Raylib.IsKeyPressed(KeyboardKey.V)) _guide.Visible = !_guide.Visible;
        if (Raylib.IsKeyPressed(KeyboardKey.M)) _guide.CycleMode();
        if (Raylib.IsKeyPressed(KeyboardKey.Z)) _guide.ChangeFanCount(-1);
        if (Raylib.IsKeyPressed(KeyboardKey.X)) _guide.ChangeFanCount(+1);

        // Move the point where the demonstration ray hits the sphere.
        if (Raylib.IsKeyDown(KeyboardKey.Q)) _guide.HitOffsetDeg -= 30f * dt;
        if (Raylib.IsKeyDown(KeyboardKey.E)) _guide.HitOffsetDeg += 30f * dt;
        _guide.HitOffsetDeg = Math.Clamp(_guide.HitOffsetDeg, 0f, 80f);

        const float angleRate = 60f; // degrees / second
        if (Raylib.IsKeyDown(KeyboardKey.Left)) _lightAzimuthDeg -= angleRate * dt;
        if (Raylib.IsKeyDown(KeyboardKey.Right)) _lightAzimuthDeg += angleRate * dt;
        if (Raylib.IsKeyDown(KeyboardKey.Up)) _lightElevationDeg += angleRate * dt;
        if (Raylib.IsKeyDown(KeyboardKey.Down)) _lightElevationDeg -= angleRate * dt;
        _lightAzimuthDeg = (_lightAzimuthDeg % 360f + 360f) % 360f;
        // Keep the bulb above the floor.
        _lightElevationDeg = Math.Clamp(_lightElevationDeg, 5f, 89f);

        if (Raylib.IsKeyDown(KeyboardKey.W)) _lightDistance -= 3f * dt;
        if (Raylib.IsKeyDown(KeyboardKey.S)) _lightDistance += 3f * dt;
        _lightDistance = Math.Clamp(_lightDistance, 3f, 15f);

        if (Raylib.IsKeyDown(KeyboardKey.Equal) || Raylib.IsKeyDown(KeyboardKey.KpAdd)) _shininess *= 1f + dt;
        if (Raylib.IsKeyDown(KeyboardKey.Minus) || Raylib.IsKeyDown(KeyboardKey.KpSubtract)) _shininess /= 1f + dt;
        _shininess = Math.Clamp(_shininess, 8f, 512f);

        if (Raylib.IsKeyDown(KeyboardKey.RightBracket)) _spinSpeedDeg += 90f * dt;
        if (Raylib.IsKeyDown(KeyboardKey.LeftBracket)) _spinSpeedDeg -= 90f * dt;

        if (Raylib.IsMouseButtonDown(MouseButton.Left))
        {
            Vector2 delta = Raylib.GetMouseDelta();
            _camYawDeg += delta.X * 0.3f;
            // Stay above the floor so its mirror reflection remains correct.
            _camPitchDeg = Math.Clamp(_camPitchDeg + delta.Y * 0.3f, 2f, 85f);
        }
        _camDistance = Math.Clamp(_camDistance - Raylib.GetMouseWheelMove() * 0.5f, 4f, 30f);
    }

    /// <summary>Renders one frame: scene off-screen, post-processing, then the HUD.</summary>
    public void Draw()
    {
        _post.TrackFrameTime(Raylib.GetFrameTime());
        _post.EnsureSize();

        _post.BeginScene();
        DrawWorld();
        _post.EndScene();
        _post.BuildBloom();

        Raylib.BeginDrawing();
        Raylib.ClearBackground(Color.Black);
        _post.Composite();
        _hud.Draw(new HudState(_lightAzimuthDeg, _lightElevationDeg, _lightDistance, _shininess,
                               _spinSpeedDeg, _paused, _post.BloomEnabled, _post.Supersample,
                               _guide.Visible ? _guide.Current : null,
                               _guide.Visible ? _guide.Mode : null, _guide.RayCount),
                  Camera, _guide.Labels());
        Raylib.EndDrawing();
    }

    private void DrawWorld()
    {
        Camera3D camera = Camera;
        Vector3 lightPos = LightPosition;
        int w = _post.SceneWidth, h = _post.SceneHeight;

        // Backdrop: dark studio, slightly lighter towards the horizon.
        Raylib.ClearBackground(new Color(5, 6, 10, 255));
        Raylib.DrawRectangleGradientV(0, 0, w, h / 2, new Color(5, 6, 10, 255), new Color(20, 21, 27, 255));
        Raylib.DrawRectangleGradientV(0, h / 2, w, h - h / 2, new Color(20, 21, 27, 255), new Color(7, 8, 11, 255));

        // Sphere transform (raylib order: scale, then spin about its centre, then move).
        Matrix4x4 sphereTransform = Raymath.MatrixMultiply(
            Raymath.MatrixMultiply(Raymath.MatrixScale(SphereRadius, SphereRadius, SphereRadius),
                                   Raymath.MatrixRotate(SpinAxis, _spinAngleDeg * Raylib.DEG2RAD)),
            Raymath.MatrixTranslate(_position.X, _position.Y, _position.Z));
        Matrix4x4 mirrorTransform = Raymath.MatrixMultiply(
            Raymath.MatrixMultiply(Raymath.MatrixTranslate(0, -FloorY, 0), Raymath.MatrixScale(1, -1, 1)),
            Raymath.MatrixTranslate(0, FloorY, 0));

        Raylib.BeginMode3D(camera);

        foreach (var shader in new[] { _sphereShader, _floorShader })
        {
            SetUniform(shader, "lightPos", lightPos);
            SetUniform(shader, "viewPos", camera.Position);
            SetUniform(shader, "sphereCenter", _position);
        }
        SetUniform(_sphereShader, "shininess", _shininess);
        _guide.Update(_position, SphereRadius, lightPos, LightRadius, FloorY);

        // 1. Reflection: the scene mirrored below the floor. The sphere shader un-mirrors
        //    each point and shades it as seen from a mirrored camera.
        SetUniform(_sphereShader, "mirrored", 1f);
        _sphere.Transform = Raymath.MatrixMultiply(sphereTransform, mirrorTransform);
        Rlgl.DisableBackfaceCulling(); // mirroring flips the triangle winding
        Raylib.DrawModel(_sphere, Vector3.Zero, 1f, Color.White);
        Rlgl.EnableBackfaceCulling();
        DrawBulb(camera, Mirror(lightPos), 0.75f);

        // 2. Polished floor, partly transparent where it reflects.
        Raylib.DrawModel(_floor, new Vector3(0, FloorY, 0), 1f, Color.White);

        // 3. The real, mirror-polished sphere.
        SetUniform(_sphereShader, "mirrored", 0f);
        _sphere.Transform = sphereTransform;
        Raylib.DrawModel(_sphere, Vector3.Zero, 1f, Color.White);

        // 4. Bounds of the sphere's motion, and the light itself.
        Raylib.DrawCubeWires(Vector3.Zero, BoxHalfExtents.X * 2, BoxHalfExtents.Y * 2, BoxHalfExtents.Z * 2,
                             new Color(48, 54, 70, 255));
        DrawBulb(camera, lightPos, 1f);

        // 5. The law-of-reflection diagram on top.
        _guide.Draw3D();

        Raylib.EndMode3D();
    }

    /// <summary>The bulb (a hot, unlit core) plus an additive glow halo.</summary>
    private void DrawBulb(Camera3D camera, Vector3 position, float brightness)
    {
        byte Level(float v) => (byte)Math.Clamp(v * brightness * 255f, 0f, 255f);
        Raylib.DrawModel(_bulb, position, 1f, new Color(Level(1f), Level(0.97f), Level(0.92f), (byte)255));

        var warm = new Color(Level(LightColor.X), Level(LightColor.Y), Level(LightColor.Z), (byte)255);
        Rlgl.DrawRenderBatchActive();    // flush pending lines before changing depth state
        Rlgl.DisableDepthMask();         // the halo is translucent: don't hide what's behind it
        Raylib.BeginBlendMode(BlendMode.Additive);
        Raylib.DrawBillboard(camera, _glow, position, 1.2f, Color.White);
        Raylib.DrawBillboard(camera, _glow, position, 4.5f, warm);
        Raylib.EndBlendMode();
        Rlgl.EnableDepthMask();
    }

    public void Dispose()
    {
        Raylib.UnloadTexture(_glow);
        Raylib.UnloadModel(_bulb);
        Raylib.UnloadModel(_floor);
        Raylib.UnloadModel(_sphere); // also unloads the mesh
        Raylib.UnloadShader(_floorShader);
        Raylib.UnloadShader(_sphereShader);
        _hud.Dispose();
        _post.Dispose();
    }
}
