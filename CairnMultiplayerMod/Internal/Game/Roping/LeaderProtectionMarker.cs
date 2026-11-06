using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Temporary read of the chain anchors. Gold sits on the leader's piton and red on the
/// follower's. With the rope tied and no leader anchor left, the HUD says so.
/// </summary>
internal static class LeaderProtectionMarker
{
    internal const string MessageId = "rope.leader-protection";
    internal const string UnprotectedText = "Leader unprotected";
    internal const float SharedOffsetX = 0.2f;
    internal const float SharedOffsetY = 0.15f;

    private static HudAdapter _hud;
    private static GameObject _leaderMarker;
    private static GameObject _followerMarker;

    internal static void Bind(HudAdapter hud) => _hud = hud;

    internal static string StatusText(bool linked, bool hasLeaderAnchor)
        => linked && !hasLeaderAnchor ? UnprotectedText : null;

    internal static void Tick()
    {
        var hasLeader = TeamAnchorSession.TryGetLeaderAnchor(out var leaderId, out var leaderPosition);
        var hasFollower = TeamAnchorSession.TryGetFollowerAnchor(out var followerId, out var followerPosition);
        if (hasLeader)
            ShowMarker(ref _leaderMarker, "CairnMP_LeaderPiton", 1f, 0.84f, 0.2f, leaderPosition);
        else
            HideMarker(_leaderMarker);

        if (hasFollower)
        {
            if (hasLeader && leaderId == followerId)
            {
                followerPosition.X += SharedOffsetX;
                followerPosition.Y += SharedOffsetY;
            }

            ShowMarker(ref _followerMarker, "CairnMP_FollowerPiton", 1f, 0.15f, 0.1f, followerPosition);
        }
        else
        {
            HideMarker(_followerMarker);
        }

        if (hasLeader)
        {
            _hud?.HideMessage(MessageId);
            return;
        }

        var text = StatusText(TeamAnchorSession.IsLinked, hasLeaderAnchor: !TeamAnchorSession.LeaderIsUnprotected);
        if (text == null) _hud?.HideMessage(MessageId);
        else _hud?.ShowMessage(MessageId, text, 0.35f);
    }

    internal static void Hide()
    {
        HideMarker(_leaderMarker);
        HideMarker(_followerMarker);
        _hud?.HideMessage(MessageId);
    }

    private static void ShowMarker(ref GameObject marker, string name, float red, float green, float blue,
        System.Numerics.Vector3 position)
    {
        try
        {
            if (marker == null)
            {
                marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.name = name;
                var collider = marker.GetComponent<Collider>();
                if (collider != null) Object.Destroy(collider);
                var renderer = marker.GetComponent<Renderer>();
                if (renderer != null) renderer.material.color = new Color(red, green, blue, 1f);
                marker.transform.localScale = Vector3.one * 0.22f;
                Object.DontDestroyOnLoad(marker);
            }

            marker.SetActive(true);
            marker.transform.position = new Vector3(position.X, position.Y, position.Z);
        }
        catch (System.Exception exception)
        {
            ModLog.SuppressedException("rope.leader-marker", exception);
        }
    }

    private static void HideMarker(GameObject marker)
    {
        if (marker != null) marker.SetActive(false);
    }
}
