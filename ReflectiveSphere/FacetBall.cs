using System.Numerics;
using Raylib_cs;

namespace ReflectiveSphere;

/// <summary>One flat mirror tile on the ball, in object space (ball radius = 1).</summary>
public readonly record struct Facet(Vector3 Center, Vector3 Normal, float Area, Vector3 Reflectance);

/// <summary>
/// A mirror ball covered in small flat tiles, laid in rows like a real disco ball.
/// Each tile is a flat mirror, so it reflects the bulb as a narrow beam at its own angle.
/// </summary>
public sealed class FacetBall
{
    public static readonly Vector3 Silver = new(0.95f, 0.95f, 0.97f);
    public static readonly Vector3 Gold = new(1.0f, 0.74f, 0.34f);

    private const int Rows = 18;
    private const float Gap = 0.09f;          // fraction of each tile left as a dark seam
    private const float TiltJitter = 0.018f;  // tiles are glued slightly unevenly (radians)

    public IReadOnlyList<Facet> Facets => _facets;
    private readonly List<Facet> _facets = new();
    private readonly List<(Vector3 A, Vector3 B, Vector3 C, Vector3 D)> _quads = new();
    private readonly List<int> _quadFacet = new();                    // which facet each quad is
    private readonly List<(Vector3[] Rim, int Facet)> _caps = new(); // one round tile at each pole

    public FacetBall(int seed = 7)
    {
        var random = new Random(seed);
        AddCap(top: true);
        AddCap(top: false);
        for (int row = 1; row < Rows - 1; row++)
        {
            float phi0 = MathF.PI * row / Rows, phi1 = MathF.PI * (row + 1) / Rows;
            float phiMid = 0.5f * (phi0 + phi1);
            // Fewer tiles near the poles, so every tile is roughly square.
            int count = Math.Max(4, (int)MathF.Round(2 * Rows * MathF.Sin(phiMid)));
            float offset = (row % 2) * 0.5f * MathF.Tau / count; // brick-like stagger

            for (int k = 0; k < count; k++)
            {
                float t0 = MathF.Tau * k / count + offset, t1 = MathF.Tau * (k + 1) / count + offset;
                Vector3 a = OnSphere(phi0, t0), b = OnSphere(phi0, t1), c = OnSphere(phi1, t1), d = OnSphere(phi1, t0);
                Vector3 centerDir = Vector3.Normalize(OnSphere(phiMid, 0.5f * (t0 + t1)));

                // Slightly uneven tile angle, like a hand-made ball.
                Vector3 jitter = new((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f);
                jitter -= Vector3.Dot(jitter, centerDir) * centerDir;
                Vector3 normal = Vector3.Normalize(centerDir + jitter * 2f * TiltJitter);

                // Flatten the four corners onto the tile's plane, then shrink them to leave a seam.
                float planeDistance = (Vector3.Dot(a, normal) + Vector3.Dot(b, normal) + Vector3.Dot(c, normal) + Vector3.Dot(d, normal)) / 4f;
                Vector3 Flatten(Vector3 p) => p - (Vector3.Dot(p, normal) - planeDistance) * normal;
                Vector3 fa = Flatten(a), fb = Flatten(b), fc = Flatten(c), fd = Flatten(d);
                Vector3 centroid = (fa + fb + fc + fd) / 4f;
                Vector3 Inset(Vector3 p) => centroid + (p - centroid) * (1f - Gap);
                fa = Inset(fa); fb = Inset(fb); fc = Inset(fc); fd = Inset(fd);

                float area = 0.5f * (Vector3.Cross(fb - fa, fc - fa).Length() + Vector3.Cross(fc - fa, fd - fa).Length());
                Vector3 reflectance = (row + k) % 2 == 0 ? Silver : Gold;
                _facets.Add(new Facet(centroid, normal, area, reflectance));
                _quads.Add((fa, fb, fc, fd));
                _quadFacet.Add(_facets.Count - 1);
            }
        }
    }

    /// <summary>A single flat, round tile covering the first (or last) row at a pole.</summary>
    private void AddCap(bool top)
    {
        float phi = MathF.PI / Rows * (top ? 1 : Rows - 1);
        Vector3 normal = top ? Vector3.UnitY : -Vector3.UnitY;
        const int points = 16;
        var rim = new Vector3[points];
        float planeDistance = MathF.Abs(MathF.Cos(phi));
        for (int i = 0; i < points; i++)
        {
            Vector3 p = OnSphere(phi, MathF.Tau * i / points);
            p -= (Vector3.Dot(p, normal) - planeDistance) * normal; // onto the tile's plane
            Vector3 center = normal * planeDistance;
            rim[i] = center + (p - center) * (1f - Gap);
        }
        float radius = (rim[0] - normal * planeDistance).Length();
        _facets.Add(new Facet(normal * planeDistance, normal, MathF.PI * radius * radius, Silver));
        _caps.Add((rim, _facets.Count - 1));
    }

    private static Vector3 OnSphere(float phi, float theta) =>
        new(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));

    /// <summary>
    /// Builds the renderable mesh: two triangles per tile with the tile's flat normal,
    /// texture coordinates across the tile (for its bevel) and its colour in the vertex colour.
    /// </summary>
    public Mesh BuildMesh()
    {
        int triangles = _quads.Count * 2 + _caps.Sum(c => c.Rim.Length);
        var mesh = new Mesh(triangles * 3, triangles);
        mesh.AllocVertices();
        mesh.AllocNormals();
        mesh.AllocTexCoords();
        mesh.AllocColors();
        Span<Vector3> vertices = mesh.VerticesAs<Vector3>();
        Span<Vector3> normals = mesh.NormalsAs<Vector3>();
        Span<Vector2> uvs = mesh.TexCoordsAs<Vector2>();
        Span<Color> colors = mesh.ColorsAs<Color>();

        int v = 0;
        for (int i = 0; i < _quads.Count; i++)
        {
            var (a, b, c, d) = _quads[i];
            Facet f = _facets[_quadFacet[i]];
            var color = new Color((byte)(f.Reflectance.X * 255), (byte)(f.Reflectance.Y * 255), (byte)(f.Reflectance.Z * 255), (byte)255);

            // Winding chosen so the tile faces outwards.
            bool outward = Vector3.Dot(Vector3.Cross(b - a, c - a), f.Normal) > 0f;
            (Vector3 p, Vector2 uv)[] corners = outward
                ? new[] { (a, new Vector2(0, 0)), (b, new Vector2(1, 0)), (c, new Vector2(1, 1)), (d, new Vector2(0, 1)) }
                : new[] { (a, new Vector2(0, 0)), (d, new Vector2(0, 1)), (c, new Vector2(1, 1)), (b, new Vector2(1, 0)) };
            foreach (int index in new[] { 0, 1, 2, 0, 2, 3 })
            {
                vertices[v] = corners[index].p;
                uvs[v] = corners[index].uv;
                normals[v] = f.Normal;
                colors[v] = color;
                v++;
            }
        }

        // Pole caps: a fan of triangles around the centre. The centre is "furthest from the
        // edge" in texture space so the shader's rounded bevel runs around the rim.
        foreach (var (rim, index) in _caps)
        {
            Facet f = _facets[index];
            var color = new Color((byte)(f.Reflectance.X * 255), (byte)(f.Reflectance.Y * 255), (byte)(f.Reflectance.Z * 255), (byte)255);
            for (int i = 0; i < rim.Length; i++)
            {
                Vector3 p1 = rim[i], p2 = rim[(i + 1) % rim.Length];
                bool outward = Vector3.Dot(Vector3.Cross(p1 - f.Center, p2 - f.Center), f.Normal) > 0f;
                foreach (var (p, uv) in outward
                             ? new[] { (f.Center, new Vector2(0.5f, 0.5f)), (p1, new Vector2(0f, 0.5f)), (p2, new Vector2(0f, 0.5f)) }
                             : new[] { (f.Center, new Vector2(0.5f, 0.5f)), (p2, new Vector2(0f, 0.5f)), (p1, new Vector2(0f, 0.5f)) })
                {
                    vertices[v] = p;
                    uvs[v] = uv;
                    normals[v] = f.Normal;
                    colors[v] = color;
                    v++;
                }
            }
        }

        Raylib.UploadMesh(ref mesh, false);
        return mesh;
    }
}
