namespace CairnMultiplayerMod.UI.Sketch;

/// <summary>
/// Valeurs de mise en page ajustables a chaud (cadre + zone de contenu), pour caler le navy dans le
/// cadre croquis asymetrique. Reglees en direct via SketchTuner (preview F9) ; les valeurs finales
/// seront figees ici. Toutes en pixels de reference (CanvasScaler 1920x1080).
/// </summary>
internal static class SketchLayout
{
    // Cadre croquis : debordement (outset) par rapport au panneau. Valeurs calees via le tuner.
    public static float FrameL = 26f;
    public static float FrameR = 22f;
    public static float FrameT = 30f;
    public static float FrameB = 18f;

    // Zone de contenu (navy) : marge (inset) a l'interieur du panneau. Valeurs calees via le tuner
    // (negatif = le navy deborde le panneau pour remplir le cadre).
    public static float ContentL = -20f;
    public static float ContentR = -4f;
    public static float ContentT = 84f;
    public static float ContentB = -4f;

    public static string Summary() =>
        $"Frame L{FrameL:0} R{FrameR:0} T{FrameT:0} B{FrameB:0}  |  " +
        $"Content L{ContentL:0} R{ContentR:0} T{ContentT:0} B{ContentB:0}";
}
