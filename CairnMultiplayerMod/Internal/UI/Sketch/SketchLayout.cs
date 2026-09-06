namespace CairnMultiplayerMod.Internal.UI.Sketch;

/// <summary>
/// Frozen layout values (frame + content area) that align the navy background inside the
/// asymmetric sketch frame. All in reference pixels (CanvasScaler 1920x1080).
/// </summary>
internal static class SketchLayout
{
    // Sketch frame: outset relative to the panel.
    public const float FrameL = 26f;
    public const float FrameR = 22f;
    public const float FrameT = 30f;
    public const float FrameB = 18f;

    // Content area (navy): inset inside the panel
    // (negative = the navy overflows the panel to fill the frame).
    public const float ContentL = -20f;
    public const float ContentR = -4f;
    public const float ContentT = 84f;
    public const float ContentB = -4f;
}
