using System;
using MelonLoader;

namespace CairnMultiplayerMod.Internal.Game.Voice;

internal enum VoiceMode { VoiceActivity, PushToTalk, Muted }

internal static class VoicePreferences
{
    internal static MelonPreferences_Entry<int> Mode;
    internal static MelonPreferences_Entry<string> Microphone;
    internal static MelonPreferences_Entry<string> PushToTalkKey;
    internal static MelonPreferences_Entry<float> ThresholdDb;
    internal static MelonPreferences_Entry<float> Volume;
    internal static VoiceMode CurrentMode => (VoiceMode)Math.Clamp(Mode.Value, 0, 2);

    internal static void Register()
    {
        var category = MelonPreferences.CreateCategory("CairnMP.Voice");
        Mode = category.CreateEntry("Mode", 0);
        Microphone = category.CreateEntry("Microphone", "");
        PushToTalkKey = category.CreateEntry("PushToTalkKey", "V");
        ThresholdDb = category.CreateEntry("ThresholdDb", -40f);
        Volume = category.CreateEntry("Volume", 1f);
    }

    internal static float SafeThreshold => float.IsFinite(ThresholdDb.Value) ? Math.Clamp(ThresholdDb.Value, -60, -10) : -40;
    internal static float SafeVolume => float.IsFinite(Volume.Value) ? Math.Clamp(Volume.Value, 0, 2) : 1;
    internal static void Save() => MelonPreferences.Save();
}
