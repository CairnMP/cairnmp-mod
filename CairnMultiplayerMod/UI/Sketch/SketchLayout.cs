namespace CairnMultiplayerMod.UI.Sketch;

/// <summary>
/// Hot-adjustable layout values (frame + content area), used to align the navy inside the asymmetric
/// sketch frame. Tuned live via SketchTuner (F9 preview); the final values will be frozen here.
/// All in reference pixels (CanvasScaler 1920x1080).
/// </summary>
internal static class SketchLayout
{
    // Sketch frame: outset relative to the panel. Values tuned via the tuner.
    public static float FrameL = 26f;
    public static float FrameR = 22f;
    public static float FrameT = 30f;
    public static float FrameB = 18f;

    // Content area (navy): inset inside the panel. Values tuned via the tuner
    // (negative = the navy overflows the panel to fill the frame).
    public static float ContentL = -20f;
    public static float ContentR = -4f;
    public static float ContentT = 84f;
    public static float ContentB = -4f;

    public static string Summary() =>
        $"Frame L{FrameL:0} R{FrameR:0} T{FrameT:0} B{FrameB:0}  |  " +
        $"Content L{ContentL:0} R{ContentR:0} T{ContentT:0} B{ContentB:0}";
}
