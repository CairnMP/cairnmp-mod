using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game.Roping;

internal static partial class RopeInterop
{
    private struct ParkedPiton
    {
        internal int Index;
        internal Lifeline.PlacedPitonData Data;
    }

    private static readonly List<ParkedPiton> Parked = new();
    private static readonly List<IntPtr> ParkScratch = new();

    /// <summary>
    /// Lifts the other climber's pitons and the planted staff off the list Cairn
    /// walks for a save. They stay in the rock. Nothing is destroyed, and the
    /// partner is not told. Own pitons stay on the list.
    /// </summary>
    internal static void ParkForeignPitons()
    {
        if (Parked.Count > 0) return;

        var placed = TryGetLifeline()?.PlacedPitons;
        if (placed == null || placed.Count == 0) return;

        ParkScratch.Clear();
        _pitons.CopyLeaving(ParkScratch);
        TopAnchorSession.CopyPointers(ParkScratch);
        if (ParkScratch.Count == 0) return;

        try
        {
            for (var index = placed.Count - 1; index >= 0; index--)
            {
                var data = placed[index];
                var piton = data?.Piton;
                if (piton == null || piton.Pointer == IntPtr.Zero) continue;
                if (!ParkScratch.Contains(piton.Pointer)) continue;
                Parked.Add(new ParkedPiton { Index = index, Data = data });
                placed.RemoveAt(index);
            }
        }
        catch (Exception exception)
        {
            ModLog.Warning("[Save] Could not lift the other climber's pitons: " + exception.Message);
            UnparkForeignPitons();
        }
    }

    /// <summary>Puts a parked piton back at the index it left, lowest index first.</summary>
    internal static void UnparkForeignPitons()
    {
        if (Parked.Count == 0)
        {
            ParkScratch.Clear();
            return;
        }

        try
        {
            var placed = TryGetLifeline()?.PlacedPitons;
            if (placed != null)
            {
                for (var n = Parked.Count - 1; n >= 0; n--)
                {
                    var parked = Parked[n];
                    var index = parked.Index;
                    if (index < 0 || index > placed.Count) index = placed.Count;
                    placed.Insert(index, parked.Data);
                }
            }
        }
        catch (Exception exception)
        {
            ModLog.Warning("[Save] Could not put the other climber's pitons back: " + exception.Message);
        }
        finally
        {
            Parked.Clear();
            ParkScratch.Clear();
        }
    }
}
