using Raylib_cs;

namespace ReflectiveSphere;

public static class Program
{
    public static void Main(string[] args)
    {
        // Optional: `--screenshot out.png` renders ~3 seconds, saves a frame and exits.
        //           `--rays single|fan|burst` picks the diagram's ray mode (and shows it).
        //           `--haze off|light|medium|thick` sets how hazy the air is.
        //           `--at 2.7` starts the motion that many seconds in.
        string? Option(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        string? screenshotFile = Option("--screenshot");
        RayMode? rayMode = Enum.TryParse(Option("--rays"), ignoreCase: true, out RayMode parsed) ? parsed : null;

        // Anti-aliasing comes from supersampling in PostProcess, so no MSAA here.
        // In screenshot mode vsync is off so the reported fps is the true rendering speed.
        var flags = ConfigFlags.HighDpiWindow | ConfigFlags.ResizableWindow;
        if (screenshotFile == null) flags |= ConfigFlags.VSyncHint;
        Raylib.SetConfigFlags(flags);
        Raylib.InitWindow(1280, 800, "Reflective Sphere");
        Raylib.SetTargetFPS(screenshotFile == null ? 60 : 0);

        using (var scene = new SphereScene())
        {
            if (rayMode is { } mode) { scene.RayMode = mode; scene.ShowDiagram = true; }
            if (Option("--haze") is { } haze) scene.Haze = haze;
            if (float.TryParse(Option("--at"), System.Globalization.CultureInfo.InvariantCulture, out float at))
                scene.Simulate(at);
            int frame = 0;
            var timer = new System.Diagnostics.Stopwatch();
            while (!Raylib.WindowShouldClose()) // Esc or window close button
            {
                scene.Update(Raylib.GetFrameTime());
                scene.Draw();

                if (screenshotFile == null) continue;
                frame++;
                if (frame == 60) timer.Start(); // measure after a 1 s warm-up
                if (frame == 180)
                {
                    // Read the framebuffer at its real pixel size (TakeScreenshot double-scales on Retina).
                    Image shot = Raylib.LoadImageFromScreen();
                    Raylib.ExportImage(shot, screenshotFile);
                    Raylib.UnloadImage(shot);
                    Console.WriteLine($"Screenshot: {shot.Width}x{shot.Height}, {120 / timer.Elapsed.TotalSeconds:0} fps average");
                    break;
                }
            }
        }

        Raylib.CloseWindow();
    }
}
