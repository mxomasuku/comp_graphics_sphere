using System.Numerics;
using Raylib_cs;

namespace ReflectiveSphere;

/// <summary>A beam of light reflected by one mirror tile.</summary>
public readonly record struct Beam(
    Vector3 Start,        // the tile's centre (world space)
    Vector3 Direction,    // reflected direction: angle of reflection = angle of incidence
    float Length,         // to the floor, or until it fades out
    bool HitsFloor,
    float StartRadius,    // half the tile's width
    float Spread,         // how fast the beam widens (radians): the lamp's size seen from the tile
    Vector3 Power);       // light power carried by the beam, per colour channel

/// <summary>
/// Simulates the light the mirror ball reflects. Each lit tile sends a narrow beam of the
/// spotlight's light off at the mirror angle. Beams are drawn as light scattered by haze in
/// the air (brightness from the beam's power and width), and where a beam lands its light
/// is added to a floor light map that the floor and ball shaders read.
/// </summary>
public sealed class LightSimulation : IDisposable
{
    public const float SpotMapExtent = 12f;   // the floor light map covers x, z in [-12, 12]
    public const float SpotMapScale = 4f;     // stored value = irradiance / scale (8-bit headroom)
    private const int SpotMapSize = 1024;
    private const float MaxBeamLength = 11f;
    private const float TileUnevenness = 0.008f; // extra spread from imperfect tiles (radians)
    private const int Segments = 10;
    private const float WidthFactor = 1.35f;   // ribbon half-width / beam radius (covers the soft edge)

    public RenderTexture2D SpotMap { get; }
    public IReadOnlyList<Beam> Beams => _beams;
    private readonly List<Beam> _beams = new();

    private readonly Texture2D _beamProfile, _spotShape;

    public LightSimulation()
    {
        SpotMap = Raylib.LoadRenderTexture(SpotMapSize, SpotMapSize);
        Raylib.SetTextureFilter(SpotMap.Texture, TextureFilter.Bilinear);
        Raylib.SetTextureWrap(SpotMap.Texture, TextureWrap.Clamp);
        _beamProfile = CreateBeamProfile();
        _spotShape = CreateSpotShape();
    }

    /// <summary>Gaussian cross-section of a beam (bright core, soft edges).</summary>
    private static Texture2D CreateBeamProfile()
    {
        const int w = 64, h = 4;
        Image image = Raylib.GenImageColor(w, h, Color.Blank);
        for (int x = 0; x < w; x++)
        {
            float u = (x + 0.5f) / w * 2f - 1f; // -1..1 across the ribbon
            byte a = (byte)(255f * MathF.Exp(-u * u * 3.2f) * (1f - u * u));
            for (int y = 0; y < h; y++)
                Raylib.ImageDrawPixel(ref image, x, y, new Color((byte)255, (byte)255, (byte)255, a));
        }
        Texture2D texture = Raylib.LoadTextureFromImage(image);
        Raylib.SetTextureFilter(texture, TextureFilter.Bilinear);
        Raylib.SetTextureWrap(texture, TextureWrap.Clamp);
        Raylib.UnloadImage(image);
        return texture;
    }

    /// <summary>A disc with a soft rim: the footprint of a beam on the floor.</summary>
    private static Texture2D CreateSpotShape()
    {
        const int size = 64;
        Image image = Raylib.GenImageColor(size, size, Color.Blank);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
            float r = MathF.Sqrt(dx * dx + dy * dy);
            float a = 1f - Smoothstep(0.55f, 1f, r);
            Raylib.ImageDrawPixel(ref image, x, y, new Color((byte)255, (byte)255, (byte)255, (byte)(a * 255f)));
        }
        Texture2D texture = Raylib.LoadTextureFromImage(image);
        Raylib.SetTextureFilter(texture, TextureFilter.Bilinear);
        Raylib.UnloadImage(image);
        return texture;
    }

    private static float Smoothstep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Traces one beam per lit tile for the ball's current position and spin.</summary>
    public void Update(FacetBall ball, Quaternion spin, Vector3 center, float radius,
                       SceneLight light, float floorY)
    {
        _beams.Clear();
        foreach (Facet f in ball.Facets)
        {
            Vector3 normal = Vector3.Transform(f.Normal, spin);
            Vector3 tile = center + Vector3.Transform(f.Center, spin) * radius;

            Vector3 toLight = light.Position - tile;
            float distance = toLight.Length();
            Vector3 l = toLight / distance;
            float cosI = Vector3.Dot(l, normal);
            if (cosI <= 0f) continue; // tile faces away from the lamp

            // Law of reflection: mirror the incoming light about the tile's normal.
            Vector3 reflected = Vector3.Reflect(-l, normal);

            // Power reaching the tile (inverse-square, tilted tiles catch less), times the
            // mirror's reflectance (gold reflects less blue).
            float irradiance = light.Intensity(-l) / (distance * distance);
            float area = f.Area * radius * radius;
            Vector3 power = f.Reflectance * light.Color * irradiance * area * cosI;

            float length = MaxBeamLength;
            bool hitsFloor = false;
            if (reflected.Y < -1e-4f)
            {
                float t = (floorY - tile.Y) / reflected.Y;
                if (t < length) { length = t; hitsFloor = true; }
            }

            float startRadius = 0.5f * MathF.Sqrt(area);
            float spread = light.Radius / distance + TileUnevenness;
            _beams.Add(new Beam(tile + reflected * 0.005f, reflected, length, hitsFloor, startRadius, spread, power));
        }
    }

    /// <summary>
    /// Paints where each beam lands into the floor light map. Irradiance is the beam's power
    /// spread over its footprint, which stretches into an ellipse when the beam arrives at a
    /// slant. Call outside any other texture mode.
    /// </summary>
    public void RenderSpotMap()
    {
        Raylib.BeginTextureMode(SpotMap);
        Raylib.ClearBackground(Color.Black);
        Raylib.BeginBlendMode(BlendMode.Additive);
        float pixelsPerUnit = SpotMapSize / (2f * SpotMapExtent);

        foreach (Beam b in _beams)
        {
            if (!b.HitsFloor) continue;
            Vector3 hit = b.Start + b.Direction * b.Length;
            float r = b.StartRadius + b.Spread * b.Length;
            float cosFloor = MathF.Max(-b.Direction.Y, 0.12f);
            Vector3 irradiance = b.Power * cosFloor / (MathF.PI * r * r);

            float major = r / cosFloor, minor = r;
            float angle = MathF.Atan2(b.Direction.Z, b.Direction.X) * Raylib.RAD2DEG;
            var dest = new Rectangle((hit.X + SpotMapExtent) * pixelsPerUnit, (hit.Z + SpotMapExtent) * pixelsPerUnit,
                                     2f * major * pixelsPerUnit, 2f * minor * pixelsPerUnit);
            Vector3 stored = irradiance / SpotMapScale;
            var color = new Color(ToByte(stored.X), ToByte(stored.Y), ToByte(stored.Z), (byte)255);
            Raylib.DrawTexturePro(_spotShape, new Rectangle(0, 0, _spotShape.Width, _spotShape.Height),
                                  dest, new Vector2(dest.Width / 2f, dest.Height / 2f), angle, color);
        }

        Raylib.EndBlendMode();
        Raylib.EndTextureMode();
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(v * 255f, 0f, 255f);

    /// <summary>
    /// Draws every beam as light scattered by haze. Seen side-on, a beam's brightness is the
    /// haze density × beam irradiance × the width of beam the eye looks through, so it is
    /// brightest near the ball and dims as it widens. Haze scatters slightly forwards, so a
    /// beam heading towards the camera looks brighter. Call inside BeginMode3D.
    /// </summary>
    public void DrawBeams(Vector3 cameraPos, float hazeDensity, bool mirrored, float floorY)
    {
        if (hazeDensity <= 0f || _beams.Count == 0) return;

        Rlgl.DrawRenderBatchActive();
        Rlgl.DisableDepthMask();
        Rlgl.DisableBackfaceCulling();
        Raylib.BeginBlendMode(BlendMode.Additive);
        Rlgl.SetTexture(_beamProfile.Id);

        Vector3 M(Vector3 p) => mirrored ? new Vector3(p.X, 2f * floorY - p.Y, p.Z) : p;

        foreach (Beam b in _beams)
        {
            // In the floor's reflection only beams that reach the floor show (and meet their
            // reflection there); skip beams too faint to see.
            if (mirrored && !b.HitsFloor) continue;
            if (BeamColor(b, 0f, b.Start, b.Direction, cameraPos, hazeDensity, false) is { R: < 3, G: < 3, B: < 3 }) continue;

            Rlgl.CheckRenderBatchLimit(Segments * 4);
            Rlgl.Begin(DrawMode.Quads);
            for (int i = 0; i < Segments; i++)
            {
                // Shorter segments near the ball, where the beam changes fastest.
                float s0 = b.Length * Ease(i / (float)Segments), s1 = b.Length * Ease((i + 1) / (float)Segments);
                Vector3 p0 = M(b.Start + b.Direction * s0), p1 = M(b.Start + b.Direction * s1);
                Vector3 axis = Vector3.Normalize(p1 - p0);
                Vector3 toCamera = Vector3.Normalize(cameraPos - 0.5f * (p0 + p1));
                Vector3 side = Vector3.Cross(axis, toCamera);
                if (side.LengthSquared() < 1e-8f) continue; // looking straight down the beam
                side = Vector3.Normalize(side);

                float w0 = WidthFactor * (b.StartRadius + b.Spread * s0), w1 = WidthFactor * (b.StartRadius + b.Spread * s1);
                Color c0 = BeamColor(b, s0, p0, axis, cameraPos, hazeDensity, i == 0);
                Color c1 = BeamColor(b, s1, p1, axis, cameraPos, hazeDensity, false);

                Rlgl.Color4ub(c0.R, c0.G, c0.B, c0.A); Rlgl.TexCoord2f(0f, 0f); Rlgl.Vertex3f(p0.X - side.X * w0, p0.Y - side.Y * w0, p0.Z - side.Z * w0);
                Rlgl.Color4ub(c0.R, c0.G, c0.B, c0.A); Rlgl.TexCoord2f(1f, 0f); Rlgl.Vertex3f(p0.X + side.X * w0, p0.Y + side.Y * w0, p0.Z + side.Z * w0);
                Rlgl.Color4ub(c1.R, c1.G, c1.B, c1.A); Rlgl.TexCoord2f(1f, 1f); Rlgl.Vertex3f(p1.X + side.X * w1, p1.Y + side.Y * w1, p1.Z + side.Z * w1);
                Rlgl.Color4ub(c1.R, c1.G, c1.B, c1.A); Rlgl.TexCoord2f(0f, 1f); Rlgl.Vertex3f(p1.X - side.X * w1, p1.Y - side.Y * w1, p1.Z - side.Z * w1);
            }
            Rlgl.End();
        }

        Rlgl.SetTexture(0);
        Raylib.EndBlendMode();
        Rlgl.EnableBackfaceCulling();
        Rlgl.EnableDepthMask();
    }

    private static float Ease(float t) => t * t * 0.6f + t * 0.4f;

    private static Color BeamColor(Beam b, float s, Vector3 p, Vector3 axis, Vector3 cameraPos, float sigma, bool start)
    {
        float r = b.StartRadius + b.Spread * s;
        Vector3 toCamera = cameraPos - p;
        float distance = toCamera.Length();
        float phase = HenyeyGreenstein(Vector3.Dot(axis, toCamera / distance), 0.35f);
        // In-scattered radiance through the beam's core: sigma * phase * (P / (pi r^2)) * 2r,
        // dimmed by haze between here and the camera and along the beam so far.
        float scale = sigma * phase * 2f / (MathF.PI * r) * MathF.Exp(-sigma * (distance + s));
        Vector3 radiance = b.Power * scale;
        if (!b.HitsFloor) radiance *= 1f - Smoothstep(0.55f, 1f, s / b.Length); // fade into the dark
        if (start) radiance *= 0.6f; // the beam leaves from a tile, not a point
        return new Color(Display(radiance.X), Display(radiance.Y), Display(radiance.Z), (byte)255);
    }

    private static float HenyeyGreenstein(float cosTheta, float g)
    {
        float g2 = g * g;
        return (1f - g2) / (4f * MathF.PI * MathF.Pow(1f + g2 - 2f * g * cosTheta, 1.5f));
    }

    /// <summary>Same ACES tone curve and gamma as the shaders, for additive drawing.</summary>
    private static byte Display(float x)
    {
        float t = Math.Clamp(x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f), 0f, 1f);
        return (byte)Math.Clamp(MathF.Pow(t, 1f / 2.2f) * 255f, 0f, 255f);
    }

    public void Dispose()
    {
        Raylib.UnloadTexture(_spotShape);
        Raylib.UnloadTexture(_beamProfile);
        Raylib.UnloadRenderTexture(SpotMap);
    }
}

/// <summary>
/// The lamp: a stage spotlight aimed at the ball, plus a soft fill light in every direction
/// so the room isn't pitch black.
/// </summary>
public readonly record struct SceneLight(
    Vector3 Position, Vector3 Aim, Vector3 Color, float Radius,
    float FillPower, float SpotPower, float CosInner, float CosOuter)
{
    /// <summary>Luminous intensity in a direction leaving the lamp.</summary>
    public float Intensity(Vector3 directionFromLamp)
    {
        float c = Vector3.Dot(directionFromLamp, Aim);
        float t = Math.Clamp((c - CosOuter) / (CosInner - CosOuter), 0f, 1f);
        return FillPower + SpotPower * t * t * (3f - 2f * t);
    }
}
