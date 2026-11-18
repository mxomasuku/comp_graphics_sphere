using System.Numerics;
using Raylib_cs;

namespace ReflectiveSphere;

/// <summary>
/// Renders the scene into a supersampled off-screen image, then builds a two-level bloom
/// (bright pass + Gaussian blur) and composites everything to the window.
/// </summary>
public sealed class PostProcess : IDisposable
{
    private readonly Shader _bright, _blur, _composite;
    private readonly int _locTexel, _locThreshold, _locDirection;
    private readonly int _locBloomNear, _locBloomFar, _locOutTexel, _locBloomStrength, _locTime;

    // Scene at supersampled size; bloom at 1/4 (near) and 1/16 (far) of that.
    private RenderTexture2D _scene, _half, _near, _nearTmp, _far, _farTmp;
    private int _renderWidth, _renderHeight;
    private float _supersample;

    // Adaptive quality: drop supersampling if frames are consistently too slow.
    private double _slowTime, _sampleTime;
    private int _sampleFrames;

    /// <summary>Scene resolution relative to the window's pixels.</summary>
    public float Supersample { get; private set; } = -1f;

    public bool BloomEnabled { get; set; } = true;

    public PostProcess(string shaderDir)
    {
        _bright = Raylib.LoadShader(null, Path.Combine(shaderDir, "bright.frag"));
        _blur = Raylib.LoadShader(null, Path.Combine(shaderDir, "blur.frag"));
        _composite = Raylib.LoadShader(null, Path.Combine(shaderDir, "composite.frag"));

        _locTexel = Raylib.GetShaderLocation(_bright, "texelSize");
        _locThreshold = Raylib.GetShaderLocation(_bright, "threshold");
        _locDirection = Raylib.GetShaderLocation(_blur, "direction");
        _locBloomNear = Raylib.GetShaderLocation(_composite, "bloomNear");
        _locBloomFar = Raylib.GetShaderLocation(_composite, "bloomFar");
        _locOutTexel = Raylib.GetShaderLocation(_composite, "outTexel");
        _locBloomStrength = Raylib.GetShaderLocation(_composite, "bloomStrength");
        _locTime = Raylib.GetShaderLocation(_composite, "time");
    }

    public int SceneWidth => _scene.Texture.Width;
    public int SceneHeight => _scene.Texture.Height;

    /// <summary>(Re)creates the off-screen buffers when the window's pixel size changes.</summary>
    public void EnsureSize()
    {
        int w = Raylib.GetRenderWidth(), h = Raylib.GetRenderHeight();
        // 2x supersampling on normal displays, 1.25x on Retina (already high resolution).
        if (Supersample < 0f) Supersample = w >= 2000 ? 1.25f : 2f;
        if (w == _renderWidth && h == _renderHeight && Supersample == _supersample) return;
        _renderWidth = w;
        _renderHeight = h;
        _supersample = Supersample;
        UnloadTargets();

        int sw = (int)(w * Supersample), sh = (int)(h * Supersample);
        _scene = CreateTarget(sw, sh);
        _half = CreateTarget(sw / 2, sh / 2);
        _near = CreateTarget(sw / 4, sh / 4);
        _nearTmp = CreateTarget(sw / 4, sh / 4);
        _far = CreateTarget(sw / 16, sh / 16);
        _farTmp = CreateTarget(sw / 16, sh / 16);
    }

    /// <summary>
    /// Call once per frame with the frame time. If the average stays below 55 fps for two
    /// seconds, step supersampling down (never below native resolution, never back up).
    /// </summary>
    public void TrackFrameTime(float dt)
    {
        if (Supersample <= 1f) return;
        _sampleTime += dt;
        _sampleFrames++;
        if (_sampleTime < 0.5) return;

        bool slow = _sampleFrames / _sampleTime < 55.0;
        _slowTime = slow ? _slowTime + _sampleTime : 0.0;
        _sampleTime = 0.0;
        _sampleFrames = 0;
        if (_slowTime >= 2.0)
        {
            Supersample = MathF.Max(1f, Supersample - (Supersample > 1.5f ? 0.5f : 0.25f));
            _slowTime = 0.0;
        }
    }

    private static RenderTexture2D CreateTarget(int w, int h)
    {
        RenderTexture2D rt = Raylib.LoadRenderTexture(Math.Max(w, 1), Math.Max(h, 1));
        Raylib.SetTextureFilter(rt.Texture, TextureFilter.Bilinear);
        Raylib.SetTextureWrap(rt.Texture, TextureWrap.Clamp);
        return rt;
    }

    public void BeginScene() => Raylib.BeginTextureMode(_scene);
    public void EndScene() => Raylib.EndTextureMode();

    /// <summary>Builds the bloom textures from the scene. Call outside BeginDrawing.</summary>
    public void BuildBloom()
    {
        if (!BloomEnabled) return;

        Pass(_scene, _half, null);                                     // exact 2x box downsample
        SetTexel(_half);
        Raylib.SetShaderValue(_bright, _locThreshold, 0.72f, ShaderUniformDataType.Float);
        Pass(_half, _near, _bright);                                   // bright parts only
        Blur(_near, _nearTmp, 2);

        SetTexel(_near);
        Raylib.SetShaderValue(_bright, _locThreshold, 0f, ShaderUniformDataType.Float);
        Pass(_near, _far, _bright);                                    // 4x downsample of the glow
        Blur(_far, _farTmp, 3);
    }

    /// <summary>Draws the final image to the window. Call inside BeginDrawing.</summary>
    public void Composite()
    {
        int screenW = Raylib.GetScreenWidth(), screenH = Raylib.GetScreenHeight();
        Raylib.SetShaderValue(_composite, _locOutTexel, new Vector2(1f / _renderWidth, 1f / _renderHeight), ShaderUniformDataType.Vec2);
        Raylib.SetShaderValue(_composite, _locBloomStrength, BloomEnabled ? 1f : 0f, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(_composite, _locTime, (float)(Raylib.GetTime() % 100.0), ShaderUniformDataType.Float);

        Raylib.BeginShaderMode(_composite);
        Raylib.SetShaderValueTexture(_composite, _locBloomNear, _near.Texture);
        Raylib.SetShaderValueTexture(_composite, _locBloomFar, _far.Texture);
        DrawFlipped(_scene, new Rectangle(0, 0, screenW, screenH));
        Raylib.EndShaderMode();
    }

    private void SetTexel(RenderTexture2D source) =>
        Raylib.SetShaderValue(_bright, _locTexel, new Vector2(1f / source.Texture.Width, 1f / source.Texture.Height), ShaderUniformDataType.Vec2);

    private void Blur(RenderTexture2D target, RenderTexture2D temp, int iterations)
    {
        var dx = new Vector2(1f / target.Texture.Width, 0f);
        var dy = new Vector2(0f, 1f / target.Texture.Height);
        for (int i = 0; i < iterations; i++)
        {
            Raylib.SetShaderValue(_blur, _locDirection, dx, ShaderUniformDataType.Vec2);
            Pass(target, temp, _blur);
            Raylib.SetShaderValue(_blur, _locDirection, dy, ShaderUniformDataType.Vec2);
            Pass(temp, target, _blur);
        }
    }

    private static void Pass(RenderTexture2D source, RenderTexture2D destination, Shader? shader)
    {
        Raylib.BeginTextureMode(destination);
        Raylib.ClearBackground(Color.Black);
        if (shader is { } s) Raylib.BeginShaderMode(s);
        DrawFlipped(source, new Rectangle(0, 0, destination.Texture.Width, destination.Texture.Height));
        if (shader is not null) Raylib.EndShaderMode();
        Raylib.EndTextureMode();
    }

    // Render textures are stored upside down, so draw them with a negative source height.
    private static void DrawFlipped(RenderTexture2D source, Rectangle destination) =>
        Raylib.DrawTexturePro(source.Texture,
            new Rectangle(0, 0, source.Texture.Width, -source.Texture.Height),
            destination, Vector2.Zero, 0f, Color.White);

    private void UnloadTargets()
    {
        foreach (var rt in new[] { _scene, _half, _near, _nearTmp, _far, _farTmp })
            if (rt.Id != 0) Raylib.UnloadRenderTexture(rt);
    }

    public void Dispose()
    {
        UnloadTargets();
        Raylib.UnloadShader(_composite);
        Raylib.UnloadShader(_blur);
        Raylib.UnloadShader(_bright);
    }
}
