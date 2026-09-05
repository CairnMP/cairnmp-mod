using System;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>Minimal in-process crash screen kept independent from the multiplayer UI.</summary>
internal static class CrashScreen
{
    private static GUIStyle _titleStyle;
    private static GUIStyle _bodyStyle;
    private static GUIStyle _pathStyle;

    public static void Draw(FatalCrashState state)
    {
        if (state == null) return;
        EnsureStyles();

        GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none);
        var width = Math.Min(760f, Screen.width - 40f);
        var height = Math.Min(430f, Screen.height - 40f);
        var panel = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
        GUI.Box(panel, GUIContent.none);

        var x = panel.x + 28f;
        var y = panel.y + 24f;
        var contentWidth = panel.width - 56f;
        GUI.Label(new Rect(x, y, contentWidth, 42f), "CairnMP stopped safely", _titleStyle);
        y += 52f;
        GUI.Label(new Rect(x, y, contentWidth, 76f),
            "A fatal error occurred in the multiplayer mod. CairnMP has stopped its network and gameplay systems to avoid corrupting your session. No diagnostic data was sent anywhere.",
            _bodyStyle);
        y += 86f;
        GUI.Label(new Rect(x, y, contentWidth, 28f), $"Error: {state.Context} — {state.Message}", _bodyStyle);
        y += 38f;

        if (!string.IsNullOrWhiteSpace(state.ArchivePath))
        {
            GUI.Label(new Rect(x, y, contentWidth, 24f), "Your local crash archive is here:", _bodyStyle);
            y += 26f;
            GUI.TextArea(new Rect(x, y, contentWidth, 56f), state.ArchivePath, _pathStyle);
            y += 68f;
            if (GUI.Button(new Rect(x, y, 170f, 38f), "Copy archive path"))
                GUIUtility.systemCopyBuffer = state.ArchivePath;
        }
        else if (state.ArchiveCompletedAtUtc == null)
        {
            GUI.Label(new Rect(x, y, contentWidth, 62f),
                "CairnMP is collecting and compressing the local logs. Cairn will remain open until this finishes.",
                _bodyStyle);
            y += 74f;
        }
        else
        {
            GUI.Label(new Rect(x, y, contentWidth, 62f),
                $"The archive could not be created: {state.ArchiveError ?? "unknown error"}\nSee the MelonLoader console for the original error.",
                _bodyStyle);
            y += 74f;
        }

        if (state.ArchiveCompletedAtUtc != null)
        {
            if (GUI.Button(new Rect(panel.xMax - 198f, panel.yMax - 62f, 170f, 38f), "Close Cairn"))
                CrashHandler.RequestClose();

            GUI.Label(new Rect(x, panel.yMax - 54f, contentWidth - 200f, 32f),
                $"Cairn will close automatically in {CrashHandler.SecondsUntilAutomaticClose}s.", _bodyStyle);
        }
    }

    private static void EnsureStyles()
    {
        if (_titleStyle != null) return;
        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 26,
            fontStyle = FontStyle.Bold,
            wordWrap = true,
        };
        _bodyStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            wordWrap = true,
        };
        _pathStyle = new GUIStyle(GUI.skin.textArea)
        {
            fontSize = 13,
            wordWrap = true,
        };
    }
}
