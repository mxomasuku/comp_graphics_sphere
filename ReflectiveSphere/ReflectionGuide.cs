using System.Numerics;
using Raylib_cs;

namespace ReflectiveSphere;

/// <summary>How many rays the reflection diagram shows.</summary>
public enum RayMode
{
    Single, // one ray, with its angles
    Fan,    // a spread of rays across the face of the ball, in one plane
    Burst,  // rings of rays over the whole lit side of the ball, in 3D
}

/// <summary>
/// Visualises the law of reflection on the sphere. Rays leave the bulb, hit the surface and
/// reflect about the surface normal. One highlighted ray also shows its normal and the two
/// (equal) angles; pulses of light travel along every ray so each bounce can be followed.
/// </summary>
public sealed class ReflectionGuide
{
    public static readonly Color IncidentColor = new(255, 196, 92, 255);
    public static readonly Color ReflectedColor = new(92, 214, 255, 255);
    public static readonly Color NormalColor = new(235, 238, 245, 255);

    private const float ArcRadius = 0.85f;
    private const float NormalLength = 1.9f;
    private const float MaxOffsetDeg = 80f;      // how far from the lit centre rays may hit
    private const float FreeRayLength = 4.5f;    // reflected rays that miss the floor
    private const float PulseSpeed = 3.5f;       // units per second
    private const float PulseSpacing = 1.6f;     // distance between pulses on a ray

    public bool Visible { get; set; } = true;
    public RayMode Mode { get; set; } = RayMode.Fan;

    /// <summary>Number of rays in the fan (odd, so one hits the centre).</summary>
    public int FanCount { get; private set; } = 9;

    /// <summary>Where the highlighted ray hits, in degrees away from the point facing
    /// the light. This is also (almost exactly) its angle of incidence.</summary>
    public float HitOffsetDeg { get; set; } = 35f;

    /// <summary>The highlighted ray, or null when the light can't reach its hit point.</summary>
    public Geometry? Current { get; private set; }

    /// <summary>All the other rays of the fan or burst.</summary>
    public IReadOnlyList<Geometry> Bundle => _bundle;
    private readonly List<Geometry> _bundle = new();

    private float _clock; // drives the light pulses; stops while paused

    public readonly record struct Geometry(
        Vector3 Hit, Vector3 Normal, Vector3 ToLight, Vector3 Reflected,
        Vector3 RayStart, Vector3 RayEnd, bool LandsOnFloor,
        float IncidenceDeg, float ReflectionDeg);

    public int RayCount => (Current is null ? 0 : 1) + _bundle.Count;

    public void CycleMode() => Mode = (RayMode)(((int)Mode + 1) % 3);

    public void ChangeFanCount(int delta) => FanCount = Math.Clamp(FanCount + 2 * delta, 3, 25);

    public void Advance(float dt) => _clock += dt;

    public void Update(Vector3 center, float radius, Vector3 lightPos, float lightRadius, Vector3 cameraPos, float floorY)
    {
        Vector3 l = Vector3.Normalize(lightPos - center);
        Vector3 v = Vector3.Normalize(cameraPos - center);

        // Put the plane of incidence face-on to the camera, so the angles are seen undistorted.
        Vector3 planeNormal = v - Vector3.Dot(v, l) * l;
        if (planeNormal.LengthSquared() < 1e-6f)
            planeNormal = Vector3.Cross(l, MathF.Abs(l.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        planeNormal = Vector3.Normalize(planeNormal);
        Vector3 w = Vector3.Normalize(Vector3.Cross(planeNormal, l));
        if (w.Y > 0f) w = -w; // tilt towards the floor so the reflected ray lands somewhere visible

        // A hit point described by how far it is from the lit centre (offset) and in
        // which direction around it (spin; 0 = within the plane facing the camera).
        Geometry? RayAt(float offsetDeg, float spinDeg)
        {
            float a = offsetDeg * Raylib.DEG2RAD, s = spinDeg * Raylib.DEG2RAD;
            Vector3 side = MathF.Cos(s) * w + MathF.Sin(s) * planeNormal;
            Vector3 normal = Vector3.Normalize(MathF.Cos(a) * l + MathF.Sin(a) * side);
            return Trace(center + radius * normal, normal, lightPos, lightRadius, floorY);
        }

        Current = RayAt(HitOffsetDeg, 0f);

        _bundle.Clear();
        switch (Mode)
        {
            case RayMode.Fan:
                // Evenly spread across the lit face, from one edge to the other.
                for (int i = 0; i < FanCount; i++)
                {
                    float offset = -MaxOffsetDeg + 2f * MaxOffsetDeg * i / (FanCount - 1);
                    if (RayAt(offset, 0f) is { } g) _bundle.Add(g);
                }
                break;

            case RayMode.Burst:
                // The centre plus rings of rays all around it.
                if (RayAt(0f, 0f) is { } c) _bundle.Add(c);
                foreach (float offset in new[] { 22f, 44f, 64f, 78f })
                {
                    int count = offset < 30f ? 6 : 10;
                    float twist = offset * 0.7f; // stagger the rings so rays don't line up
                    for (int k = 0; k < count; k++)
                        if (RayAt(offset, twist + 360f * k / count) is { } g) _bundle.Add(g);
                }
                break;
        }
    }

    /// <summary>Follows one ray: bulb → hit point → reflected, until it reaches the floor.</summary>
    private static Geometry? Trace(Vector3 hit, Vector3 normal, Vector3 lightPos, float lightRadius, float floorY)
    {
        Vector3 toLight = Vector3.Normalize(lightPos - hit);
        float cosI = Vector3.Dot(toLight, normal);
        if (cosI <= 0.02f) return null; // the light can't reach this point

        // Law of reflection: mirror the incoming direction about the normal.
        Vector3 reflected = Vector3.Reflect(-toLight, normal);

        // Measure both angles independently from the normal.
        float incidence = MathF.Acos(Math.Clamp(cosI, -1f, 1f)) * Raylib.RAD2DEG;
        float reflection = MathF.Acos(Math.Clamp(Vector3.Dot(reflected, normal), -1f, 1f)) * Raylib.RAD2DEG;

        Vector3 start = lightPos - toLight * lightRadius * 1.3f;
        bool lands = false;
        Vector3 end = hit + reflected * FreeRayLength;
        if (reflected.Y < -1e-3f)
        {
            float t = (floorY - hit.Y) / reflected.Y;
            if (t < 10f) { end = hit + reflected * t; lands = true; }
        }

        return new Geometry(hit, normal, toLight, reflected, start, end, lands, incidence, reflection);
    }

    /// <summary>Draws the diagram; call inside BeginMode3D.</summary>
    public void Draw3D()
    {
        if (!Visible) return;

        // Faint "x-ray" pass so rays stay readable where they pass behind the sphere,
        // then the normal depth-tested pass on top.
        Rlgl.DrawRenderBatchActive();
        Rlgl.DisableDepthTest();
        DrawAll(0.25f);
        Rlgl.DrawRenderBatchActive();
        Rlgl.EnableDepthTest();
        DrawAll(1f);
        DrawPulses();
        Rlgl.DrawRenderBatchActive();
    }

    private void DrawAll(float opacity)
    {
        foreach (var g in _bundle) DrawBundleRay(g, opacity);
        if (Current is { } main) DrawMainRay(main, opacity);
    }

    /// <summary>World-space positions and text for the highlighted ray's labels.</summary>
    public IEnumerable<(Vector3 Position, string Text, Color Color)> Labels()
    {
        if (!Visible || Current is not { } g) yield break;
        // Each angle label sits just outside its arc, leaning towards its own ray so the
        // two labels and the normal's label never overlap.
        yield return (g.Hit + Vector3.Normalize(g.Normal + 2f * g.ToLight) * ArcRadius * 1.45f, $"θi {g.IncidenceDeg:0.0}°", IncidentColor);
        yield return (g.Hit + Vector3.Normalize(g.Normal + 2f * g.Reflected) * ArcRadius * 1.45f, $"θr {g.ReflectionDeg:0.0}°", ReflectedColor);
        yield return (g.Hit + g.Normal * (NormalLength + 0.25f), "normal", NormalColor);
    }

    private static Color Fade(Color c, float opacity) => new(c.R, c.G, c.B, (byte)Math.Clamp(c.A * opacity, 0f, 255f));

    /// <summary>The highlighted ray: thick, with its normal and both angle arcs.</summary>
    private static void DrawMainRay(Geometry g, float opacity)
    {
        Arrow(g.RayStart, g.Hit, Fade(IncidentColor, opacity), 0.02f);
        DrawReflected(g, Fade(ReflectedColor, opacity), 0.02f, arrow: true);
        Dashed(g.Hit, g.Hit + g.Normal * NormalLength, Fade(NormalColor, opacity), 0.01f);

        Arc(g.Hit, g.Normal, g.ToLight, ArcRadius, Fade(IncidentColor, opacity));
        Arc(g.Hit, g.Normal, g.Reflected, ArcRadius, Fade(ReflectedColor, opacity));

        Raylib.DrawSphere(g.Hit, 0.045f, Fade(Color.White, opacity));
    }

    /// <summary>One of the many rays: thinner, with a short normal tick at its hit point.</summary>
    private static void DrawBundleRay(Geometry g, float opacity)
    {
        Raylib.DrawCylinderEx(g.RayStart, g.Hit, 0.008f, 0.008f, 8, Fade(IncidentColor, opacity * 0.75f));
        DrawReflected(g, Fade(ReflectedColor, opacity * 0.85f), 0.008f, arrow: false);
        Raylib.DrawCylinderEx(g.Hit, g.Hit + g.Normal * 0.3f, 0.005f, 0.005f, 6, Fade(NormalColor, opacity * 0.5f));
        Raylib.DrawSphereEx(g.Hit, 0.022f, 6, 8, Fade(Color.White, opacity));
    }

    /// <summary>The outgoing ray: ends on the floor with a marker, or fades out in mid-air.</summary>
    private static void DrawReflected(Geometry g, Color color, float radius, bool arrow)
    {
        if (g.LandsOnFloor)
        {
            if (arrow) Arrow(g.Hit, g.RayEnd, color, radius);
            else Raylib.DrawCylinderEx(g.Hit, g.RayEnd, radius, radius, 8, color);

            // Where the reflected light lands on the floor.
            Vector3 up = new(0, 0.004f, 0);
            float size = arrow ? 1f : 0.45f;
            Raylib.DrawCylinderEx(g.RayEnd + up, g.RayEnd + up * 2, 0.16f * size, 0.16f * size, 24, Fade(color, 0.35f));
            Raylib.DrawCylinderEx(g.RayEnd + up * 2, g.RayEnd + up * 3, 0.06f * size, 0.06f * size, 16, color);
            return;
        }

        // Solid for most of its length, then fading out in a few steps.
        Vector3 dir = g.RayEnd - g.Hit;
        Vector3 solidEnd = g.Hit + dir * 0.6f;
        Raylib.DrawCylinderEx(g.Hit, solidEnd, radius, radius, 8, color);
        const int steps = 4;
        for (int i = 0; i < steps; i++)
        {
            Vector3 a = solidEnd + dir * (0.4f * i / steps);
            Vector3 b = solidEnd + dir * (0.4f * (i + 1) / steps);
            Raylib.DrawCylinderEx(a, b, radius, radius, 8, Fade(color, 1f - (i + 1f) / (steps + 1f)));
        }
    }

    /// <summary>Small bright pulses travelling bulb → ball → outwards along every ray.</summary>
    private void DrawPulses()
    {
        foreach (var g in _bundle.Concat(Current is { } m ? new[] { m } : Array.Empty<Geometry>()))
        {
            float inLength = Vector3.Distance(g.RayStart, g.Hit);
            float outLength = Vector3.Distance(g.Hit, g.RayEnd);
            float total = inLength + outLength;
            for (float s = (_clock * PulseSpeed) % PulseSpacing; s < total; s += PulseSpacing)
            {
                bool incoming = s < inLength;
                Vector3 p = incoming
                    ? g.RayStart + (g.Hit - g.RayStart) * (s / inLength)
                    : g.Hit + g.Reflected * (s - inLength);
                float fade = incoming || g.LandsOnFloor ? 1f : Math.Clamp((total - s) / (0.4f * outLength), 0f, 1f);
                Color color = incoming ? IncidentColor : ReflectedColor;
                Raylib.DrawSphereEx(p, 0.04f, 6, 8, Fade(new Color(
                    (byte)((color.R + 255) / 2), (byte)((color.G + 255) / 2), (byte)((color.B + 255) / 2), (byte)255), fade));
            }
        }
    }

    /// <summary>A thick line with an arrowhead at the end.</summary>
    private static void Arrow(Vector3 from, Vector3 to, Color color, float radius)
    {
        Vector3 dir = Vector3.Normalize(to - from);
        const float headLength = 0.2f;
        Vector3 shaftEnd = to - dir * headLength;
        Raylib.DrawCylinderEx(from, shaftEnd, radius, radius, 10, color);
        Raylib.DrawCylinderEx(shaftEnd, to, radius * 3.2f, 0f, 16, color);
    }

    private static void Dashed(Vector3 from, Vector3 to, Color color, float radius)
    {
        Vector3 dir = Vector3.Normalize(to - from);
        float length = Vector3.Distance(from, to);
        const float dash = 0.1f, gap = 0.07f;
        for (float s = 0f; s < length; s += dash + gap)
            Raylib.DrawCylinderEx(from + dir * s, from + dir * MathF.Min(s + dash, length), radius, radius, 6, color);
    }

    /// <summary>An arc of the given radius around <paramref name="center"/>, from direction a to b.</summary>
    private static void Arc(Vector3 center, Vector3 a, Vector3 b, float radius, Color color)
    {
        const int segments = 28;
        Vector3 previous = center + a * radius;
        for (int i = 1; i <= segments; i++)
        {
            Vector3 next = center + Slerp(a, b, i / (float)segments) * radius;
            Raylib.DrawCylinderEx(previous, next, 0.016f, 0.016f, 6, color);
            previous = next;
        }
    }

    private static Vector3 Slerp(Vector3 a, Vector3 b, float t)
    {
        float cos = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        float angle = MathF.Acos(cos);
        if (angle < 1e-4f) return a;
        float sin = MathF.Sin(angle);
        return (MathF.Sin((1 - t) * angle) * a + MathF.Sin(t * angle) * b) / sin;
    }
}
