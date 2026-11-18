using System.Numerics;
using Raylib_cs;

namespace ReflectiveSphere;

public readonly record struct HudState(
    float AzimuthDeg, float ElevationDeg, float LightDistance, float Shininess,
    float SpinDeg, bool Paused, bool Bloom, float Supersample, ReflectionGuide.Geometry? Reflection,
    RayMode? Rays, int RayCount);

/// <summary>On-screen readouts and control hints, drawn on top of the finished image.</summary>
public sealed class Hud : IDisposable
{
    private static readonly Color Panel = new(8, 10, 16, 175);
    private static readonly Color Label = new(130, 140, 160, 255);
    private static readonly Color Value = new(232, 236, 244, 255);
    private static readonly Color Accent = new(255, 200, 120, 255);

    private readonly Font _font, _bold;
    private readonly bool _ownsFonts;

    public Hud()
    {
        const string regular = "/System/Library/Fonts/Supplemental/Arial.ttf";
        const string bold = "/System/Library/Fonts/Supplemental/Arial Bold.ttf";
        if (File.Exists(regular) && File.Exists(bold))
        {
            // Printable ASCII plus the degree and theta signs, rasterised large for crisp scaling.
            int[] codepoints = Enumerable.Range(32, 95).Append(176).Append(952).ToArray(); // + ° and θ
            _font = Raylib.LoadFontEx(regular, 64, codepoints, codepoints.Length);
            _bold = Raylib.LoadFontEx(bold, 64, codepoints, codepoints.Length);
            Raylib.SetTextureFilter(_font.Texture, TextureFilter.Bilinear);
            Raylib.SetTextureFilter(_bold.Texture, TextureFilter.Bilinear);
            _ownsFonts = true;
        }
        else
        {
            _font = _bold = Raylib.GetFontDefault();
        }
    }

    public void Draw(HudState s, Camera3D camera, IEnumerable<(Vector3 Position, string Text, Color Color)> labels)
    {
        // Angle labels next to the arcs of the reflection diagram
        foreach (var (position, text, color) in labels)
        {
            Vector2 p = Raylib.GetWorldToScreen(position, camera);
            Vector2 textSize = Raylib.MeasureTextEx(_bold, text, 17, 0.5f);
            var pill = new Rectangle(p.X - textSize.X / 2 - 8, p.Y - textSize.Y / 2 - 4, textSize.X + 16, textSize.Y + 8);
            Raylib.DrawRectangleRounded(pill, 0.5f, 8, Panel);
            Text(_bold, text, pill.X + 8, pill.Y + 4, 17, color);
        }

        // Readout panel
        var panel = new Rectangle(16, 16, 300, s.Rays is null ? 168 : s.Reflection is null ? 239 : 285);
        Raylib.DrawRectangleRounded(panel, 0.06f, 8, Panel);
        Text(_bold, "REFLECTIVE SPHERE", 32, 28, 17, Accent);

        float y = 60;
        Row("Light azimuth", $"{s.AzimuthDeg:0}°", ref y);
        Row("Light elevation", $"{s.ElevationDeg:0}°", ref y);
        Row("Light distance", $"{s.LightDistance:0.0}", ref y);
        Row("Shininess", $"{s.Shininess:0}", ref y);
        Row("Spin", $"{s.SpinDeg:0}°/s", ref y);

        if (s.Rays is { } mode)
        {
            y += 8;
            Raylib.DrawLineEx(new Vector2(32, y), new Vector2(300, y), 1f, new Color(60, 66, 84, 255));
            y += 10;
            Text(_bold, "LAW OF REFLECTION", 32, y, 14, Label);
            y += 22;
            string modeName = mode switch { RayMode.Fan => "fan", RayMode.Burst => "3D burst", _ => "single" };
            Row("Rays", $"{s.RayCount}  ({modeName})", ref y);
            if (s.Reflection is { } r)
            {
                Row("Angle of incidence θi", $"{r.IncidenceDeg:0.0}°", ref y, ReflectionGuide.IncidentColor);
                Row("Angle of reflection θr", $"{r.ReflectionDeg:0.0}°", ref y, ReflectionGuide.ReflectedColor);
            }
        }

        // Status tags (top right)
        float right = Raylib.GetScreenWidth() - 16;
        string status = $"{Raylib.GetFPS()} fps   render {s.Supersample:0.##}x   bloom {(s.Bloom ? "on" : "off")}{(s.Paused ? "   PAUSED" : "")}";
        Vector2 size = Raylib.MeasureTextEx(_font, status, 15, 0.5f);
        Raylib.DrawRectangleRounded(new Rectangle(right - size.X - 24, 16, size.X + 24, 30), 0.4f, 8, Panel);
        Text(_font, status, right - size.X - 12, 23, 15, s.Paused ? Accent : Label);

        // Controls bar: wraps onto a second row when the window is too narrow.
        int h = Raylib.GetScreenHeight(), w = Raylib.GetScreenWidth();
        var controls = new[]
        {
            ("Arrows", "light angle"), ("W/S", "light distance"), ("+/-", "shininess"), ("[ ]", "spin"),
            ("M", "ray mode"), ("Z/X", "fewer/more rays"), ("Q/E", "hit point"), ("V", "diagram"),
            ("Drag", "orbit"), ("Wheel", "zoom"), ("B", "bloom"), ("Space", "pause"), ("R", "reset"),
        };
        const float rowHeight = 30f, gap = 18f;
        float ItemWidth(string key, string action) =>
            Raylib.MeasureTextEx(_bold, key, 13, 0.5f).X + 18 + Raylib.MeasureTextEx(_font, action, 15, 0.5f).X + gap;

        var rows = new List<List<(string, string)>> { new() };
        float rowWidth = 16;
        foreach (var item in controls)
        {
            float itemWidth = ItemWidth(item.Item1, item.Item2);
            if (rowWidth + itemWidth > w - 16 && rows[^1].Count > 0) { rows.Add(new()); rowWidth = 16; }
            rows[^1].Add(item);
            rowWidth += itemWidth;
        }

        float barHeight = rows.Count * rowHeight + 12;
        Raylib.DrawRectangle(0, (int)(h - barHeight), w, (int)barHeight, Panel);
        float y0 = h - barHeight + 9;
        foreach (var row in rows)
        {
            float x = 16;
            foreach (var (key, action) in row)
            {
                x = Key(key, x, y0);
                Text(_font, action, x, y0 + 2, 15, Label);
                x += Raylib.MeasureTextEx(_font, action, 15, 0.5f).X + gap;
            }
            y0 += rowHeight;
        }
    }

    private void Row(string label, string value, ref float y, Color? valueColor = null)
    {
        Text(_font, label, 32, y, 16, Label);
        Vector2 size = Raylib.MeasureTextEx(_bold, value, 16, 0.5f);
        Text(_bold, value, 300 - size.X, y, 16, valueColor ?? Value);
        y += 23;
    }

    /// <summary>Draws a key cap and returns the x position after it.</summary>
    private float Key(string key, float x, float y)
    {
        Vector2 size = Raylib.MeasureTextEx(_bold, key, 13, 0.5f);
        var cap = new Rectangle(x, y, size.X + 12, 21);
        Raylib.DrawRectangleRounded(cap, 0.35f, 6, new Color(48, 54, 70, 230));
        Text(_bold, key, x + 6, y + 4, 13, Value);
        return x + cap.Width + 6;
    }

    private static void Text(Font font, string text, float x, float y, float size, Color color) =>
        Raylib.DrawTextEx(font, text, new Vector2(x, y), size, 0.5f, color);

    public void Dispose()
    {
        if (!_ownsFonts) return;
        Raylib.UnloadFont(_font);
        Raylib.UnloadFont(_bold);
    }
}
