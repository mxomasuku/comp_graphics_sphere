using System.Numerics;
using Raylib_cs;

namespace ReflectiveSphere;

/// <summary>
/// A mirror ball that bounces around a box in x, y and z and spins about its own axis.
/// A spotlight shines on it from a chosen angle; every flat mirror tile reflects a beam of
/// that light at the mirror angle, visible in hazy air and landing as spots on the floor.
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
    private float _spinSpeedDeg = 40f; // degrees / second
    private bool _paused;

    // The lamp: a spotlight positioned by two angles and a distance, always aimed at the ball
    private float _lightAzimuthDeg = 45f;   // around the vertical (Y) axis
    private float _lightElevationDeg = 35f; // above the horizon
    private float _lightDistance = 6.5f;
    private const float LightRadius = 0.16f; // the lens
    private static readonly Vector3 LightColor = new(1.0f, 0.88f, 0.72f); // warm, like a tungsten lamp
    private const float FillPower = 6f;      // dim light in every direction
    private const float SpotPower = 650f;    // the focused beam on the ball
    private float _shininess = 220f;

    // Haze in the air makes light beams visible
    private static readonly float[] HazeLevels = { 0f, 0.04f, 0.08f, 0.14f };
    private static readonly string[] HazeNames = { "off", "light", "medium", "thick" };
    private int _hazeLevel = 2;

    // Orbit camera
    private float _camYawDeg = 90f, _camPitchDeg = 15f, _camDistance = 13f;

    private readonly PostProcess _post;
    private readonly Hud _hud;
    private readonly ReflectionGuide _guide = new() { Visible = false };
    private readonly FacetBall _ball = new();
    private readonly LightSimulation _light = new();
    private readonly Shader _sphereShader, _floorShader, _skyShader, _hazeShader;
    private readonly Shader[] _litShaders;
    private Model _sphere, _backing, _floor;
    private readonly Texture2D _glow;

    public SphereScene()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        _post = new PostProcess(dir);
        _hud = new Hud();

        // The surface shaders share uniforms and helper functions from common.glsl.
        string vert = File.ReadAllText(Path.Combine(dir, "lit.vert"));
        string common = "#version 330\n" + File.ReadAllText(Path.Combine(dir, "common.glsl")) + "\n";
        _sphereShader = Raylib.LoadShaderFromMemory(vert, common + File.ReadAllText(Path.Combine(dir, "sphere.frag")));
        _floorShader = Raylib.LoadShaderFromMemory(vert, common + File.ReadAllText(Path.Combine(dir, "floor.frag")));
        _skyShader = Raylib.LoadShaderFromMemory(null, common + File.ReadAllText(Path.Combine(dir, "sky.frag")));
        _hazeShader = Raylib.LoadShaderFromMemory(null, common + File.ReadAllText(Path.Combine(dir, "haze.frag")));
        _litShaders = new[] { _sphereShader, _floorShader, _skyShader, _hazeShader };

        SetUniform(_sphereShader, "specularStrength", 1.0f);
        foreach (var shader in _litShaders)
        {
            SetUniform(shader, "lightColor", LightColor);
            SetUniform(shader, "lightPower", FillPower);
            SetUniform(shader, "spotPower", SpotPower);
            SetUniform(shader, "lightRadius", LightRadius);
            SetUniform(shader, "sphereRadius", SphereRadius);
            SetUniform(shader, "floorY", FloorY);
        }

        // The mirror ball: flat tiles over a dark core that shows through the seams.
        _sphere = Raylib.LoadModelFromMesh(_ball.BuildMesh());
        _backing = Raylib.LoadModelFromMesh(Raylib.GenMeshSphere(0.975f, 48, 96));
        _floor = Raylib.LoadModelFromMesh(Raylib.GenMeshPlane(60f, 60f, 1, 1));
        unsafe
        {
            _sphere.Materials[0].Shader = _sphereShader;
            _floor.Materials[0].Shader = _floorShader;
            // Both read the floor light map (spots reflected by the tiles) as texture0.
            _sphere.Materials[0].Maps[(int)MaterialMapIndex.Albedo].Texture = _light.SpotMap.Texture;
            _floor.Materials[0].Maps[(int)MaterialMapIndex.Albedo].Texture = _light.SpotMap.Texture;
        }

        _glow = CreateGlowTexture(256);
        Reset();
    }

    /// <summary>A soft radial falloff used as a camera-facing glare on the lamp's lens.</summary>
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

    private static void SetUniform(Shader shader, string name, Vector2 value) =>
        Raylib.SetShaderValue(shader, Raylib.GetShaderLocation(shader, name), value, ShaderUniformDataType.Vec2);

    /// <summary>How many rays the reflection diagram shows.</summary>
    public RayMode RayMode
    {
        get => _guide.Mode;
        set => _guide.Mode = value;
    }

    /// <summary>Haze level by name: off, light, medium or thick.</summary>
    public string Haze
    {
        get => HazeNames[_hazeLevel];
        set { int i = Array.IndexOf(HazeNames, value.ToLowerInvariant()); if (i >= 0) _hazeLevel = i; }
    }

    /// <summary>Whether the lines-and-angles diagram is drawn over the simulation.</summary>
    public bool ShowDiagram
    {
        get => _guide.Visible;
        set => _guide.Visible = value;
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

    /// <summary>The spotlight, aimed at the ball with a cone just wide enough to cover it.</summary>
    private SceneLight Light
    {
        get
        {
            Vector3 position = LightPosition;
            Vector3 toBall = _position - position;
            float distance = toBall.Length();
            float halfAngle = MathF.Asin(Math.Clamp(SphereRadius / distance, 0f, 0.95f));
            return new SceneLight(position, toBall / distance, LightColor, LightRadius, FillPower, SpotPower,
                                  MathF.Cos(halfAngle * 1.05f), MathF.Cos(halfAngle * 1.45f));
        }
    }

    private Quaternion Spin => Quaternion.CreateFromAxisAngle(SpinAxis, _spinAngleDeg * Raylib.DEG2RAD);

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

    /// <summary>Advances the ball's motion by the given time, as if that much had passed.</summary>
    public void Simulate(float seconds)
    {
        const float step = 1f / 120f;
        for (float t = 0f; t < seconds; t += step) Advance(step);
    }

    public void Update(float dt)
    {
        HandleInput(dt);
        if (!_paused) Advance(dt);
    }

    private void Advance(float dt)
    {
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
        if (Raylib.IsKeyPressed(KeyboardKey.H)) _hazeLevel = (_hazeLevel + 1) % HazeLevels.Length;
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
        // Keep the lamp above the floor.
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

    /// <summary>Renders one frame: light simulation, scene off-screen, post-processing, HUD.</summary>
    public void Draw()
    {
        _post.TrackFrameTime(Raylib.GetFrameTime());
        _post.EnsureSize();

        // Trace every tile's reflected beam and paint where they land on the floor.
        SceneLight light = Light;
        _light.Update(_ball, Spin, _position, SphereRadius, light, FloorY);
        _light.RenderSpotMap();

        _post.BeginScene();
        DrawWorld(light);
        _post.EndScene();

        // The haze glow, at half resolution in its own pass (uniforms were set in DrawWorld).
        SetCameraRays(_hazeShader, Camera, _post.HazeWidth, _post.HazeHeight);
        _post.RenderHaze(HazeLevels[_hazeLevel] > 0f ? _hazeShader : null);
        _post.BuildBloom();

        Raylib.BeginDrawing();
        Raylib.ClearBackground(Color.Black);
        _post.Composite();
        _hud.Draw(new HudState(_lightAzimuthDeg, _lightElevationDeg, _lightDistance, _shininess,
                               _spinSpeedDeg, _paused, _post.BloomEnabled, _post.Supersample,
                               HazeNames[_hazeLevel], _light.Beams.Count,
                               _guide.Visible ? _guide.Current : null,
                               _guide.Visible ? _guide.Mode : null, _guide.RayCount),
                  Camera, _guide.Labels());
        Raylib.EndDrawing();
    }

    private void DrawWorld(SceneLight light)
    {
        Camera3D camera = Camera;
        float haze = HazeLevels[_hazeLevel];

        foreach (var shader in _litShaders)
        {
            SetUniform(shader, "lightPos", light.Position);
            SetUniform(shader, "spotDir", light.Aim);
            SetUniform(shader, "spotCosInner", light.CosInner);
            SetUniform(shader, "spotCosOuter", light.CosOuter);
            SetUniform(shader, "viewPos", camera.Position);
            SetUniform(shader, "sphereCenter", _position);
            SetUniform(shader, "hazeDensity", haze);
        }
        SetUniform(_sphereShader, "shininess", _shininess);

        // Background: the dark studio behind everything.
        int w = _post.SceneWidth, h = _post.SceneHeight;
        SetCameraRays(_skyShader, camera, w, h);
        Raylib.ClearBackground(Color.Black);
        Raylib.BeginShaderMode(_skyShader);
        Raylib.DrawRectangle(0, 0, w, h, Color.White);
        Raylib.EndShaderMode();

        // Ball transform (raylib order: scale, then spin about its centre, then move).
        Matrix4x4 sphereTransform = Raymath.MatrixMultiply(
            Raymath.MatrixMultiply(Raymath.MatrixScale(SphereRadius, SphereRadius, SphereRadius),
                                   Raymath.MatrixRotate(SpinAxis, _spinAngleDeg * Raylib.DEG2RAD)),
            Raymath.MatrixTranslate(_position.X, _position.Y, _position.Z));
        Matrix4x4 mirrorTransform = Raymath.MatrixMultiply(
            Raymath.MatrixMultiply(Raymath.MatrixTranslate(0, -FloorY, 0), Raymath.MatrixScale(1, -1, 1)),
            Raymath.MatrixTranslate(0, FloorY, 0));

        Quaternion spin = Spin;
        _guide.Update(_position, SphereRadius, light.Position, LightRadius, camera.Position, FloorY,
                      dir => NearestTile(dir, spin));

        Raylib.BeginMode3D(camera);

        // 1. Reflection: the scene mirrored below the floor. The ball shader un-mirrors each
        //    point and shades it as seen from a mirrored camera.
        SetUniform(_sphereShader, "mirrored", 1f);
        Rlgl.DisableBackfaceCulling(); // mirroring flips the triangle winding
        _sphere.Transform = Raymath.MatrixMultiply(sphereTransform, mirrorTransform);
        Raylib.DrawModel(_sphere, Vector3.Zero, 1f, Color.White);
        _backing.Transform = _sphere.Transform;
        Raylib.DrawModel(_backing, Vector3.Zero, 1f, new Color(8, 8, 10, 255));
        Rlgl.EnableBackfaceCulling();
        DrawLamp(camera, light, mirrored: true);
        _light.DrawBeams(camera.Position, haze, mirrored: true, FloorY);

        // 2. Polished floor, partly transparent where it reflects.
        Raylib.DrawModel(_floor, new Vector3(0, FloorY, 0), 1f, Color.White);

        // 3. The real mirror ball.
        SetUniform(_sphereShader, "mirrored", 0f);
        _sphere.Transform = sphereTransform;
        Raylib.DrawModel(_sphere, Vector3.Zero, 1f, Color.White);
        _backing.Transform = sphereTransform;
        Raylib.DrawModel(_backing, Vector3.Zero, 1f, new Color(8, 8, 10, 255));

        // 4. Bounds of the ball's motion, the lamp, and the reflected beams in the haze.
        Raylib.DrawCubeWires(Vector3.Zero, BoxHalfExtents.X * 2, BoxHalfExtents.Y * 2, BoxHalfExtents.Z * 2,
                             new Color(40, 45, 58, 255));
        DrawLamp(camera, light, mirrored: false);
        _light.DrawBeams(camera.Position, haze, mirrored: false, FloorY);

        // 5. Optional lines-and-angles diagram on top.
        _guide.Draw3D();

        Raylib.EndMode3D();
    }

    /// <summary>Gives a full-screen shader what it needs to turn each pixel into a view ray.</summary>
    private static void SetCameraRays(Shader shader, Camera3D camera, int width, int height)
    {
        Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, camera.Up));
        SetUniform(shader, "resolution", new Vector2(width, height));
        SetUniform(shader, "camForward", forward);
        SetUniform(shader, "camRight", right);
        SetUniform(shader, "camUp", Vector3.Cross(right, forward));
        SetUniform(shader, "tanHalfFov", MathF.Tan(camera.FovY * 0.5f * Raylib.DEG2RAD));
        SetUniform(shader, "aspect", width / (float)height);
    }

    /// <summary>The tile whose direction from the ball's centre is closest to dir (world space).</summary>
    private (Vector3 Center, Vector3 Normal) NearestTile(Vector3 dir, Quaternion spin)
    {
        Vector3 local = Vector3.Transform(dir, Quaternion.Conjugate(spin));
        Facet best = _ball.Facets[0];
        float bestDot = float.MinValue;
        foreach (Facet f in _ball.Facets)
        {
            float d = Vector3.Dot(f.Center, local);
            if (d > bestDot) { bestDot = d; best = f; }
        }
        return (_position + Vector3.Transform(best.Center, spin) * SphereRadius, Vector3.Transform(best.Normal, spin));
    }

    /// <summary>A stage spotlight: dark housing pointing at the ball, a hot lens, lens glare.</summary>
    private void DrawLamp(Camera3D camera, SceneLight light, bool mirrored)
    {
        Vector3 M(Vector3 p) => mirrored ? Mirror(p) : p;
        Vector3 lens = M(light.Position);
        Vector3 back = M(light.Position - light.Aim * 0.55f);
        float dim = mirrored ? 0.7f : 1f;

        Raylib.DrawCylinderEx(back, lens, 0.24f, 0.27f, 32, new Color(7, 7, 9, 255));
        Raylib.DrawCylinderEx(lens, M(light.Position + light.Aim * 0.01f), 0.2f, 0.2f, 32,
                              new Color((byte)(255 * dim), (byte)(250 * dim), (byte)(238 * dim), (byte)255));

        Rlgl.DrawRenderBatchActive();    // flush before changing depth state
        Rlgl.DisableDepthMask();         // glare is translucent: don't hide what's behind it
        Raylib.BeginBlendMode(BlendMode.Additive);
        // Brighter when the lens faces the camera, as with a real spotlight.
        Vector3 toCamera = Vector3.Normalize(camera.Position - lens);
        Vector3 aim = mirrored ? Vector3.Normalize(Mirror(light.Position + light.Aim) - lens) : light.Aim;
        float facing = MathF.Pow(Math.Max(Vector3.Dot(toCamera, aim), 0f), 2f);
        byte glare = (byte)(Math.Clamp(0.35f + 0.65f * facing, 0f, 1f) * 255 * dim);
        Raylib.DrawBillboard(camera, _glow, lens + aim * 0.02f, 1.6f, new Color(glare, glare, glare, (byte)255));
        Raylib.EndBlendMode();
        Rlgl.EnableDepthMask();
    }

    public void Dispose()
    {
        Raylib.UnloadTexture(_glow);
        Raylib.UnloadModel(_floor);
        Raylib.UnloadModel(_backing);
        Raylib.UnloadModel(_sphere); // also unloads the mesh
        Raylib.UnloadShader(_hazeShader);
        Raylib.UnloadShader(_skyShader);
        Raylib.UnloadShader(_floorShader);
        Raylib.UnloadShader(_sphereShader);
        _light.Dispose();
        _hud.Dispose();
        _post.Dispose();
    }
}
