using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2CppSteamworks;

namespace CairnMultiplayerMod.Internal.Networking;

internal static class LobbyListResultCount
{
    internal static int Validate(uint count, int limit)
    {
        if (limit < 0 || count > (uint)limit)
            throw new InvalidDataException($"Steam returned an invalid lobby count ({count}, limit {limit}).");
        return (int)count;
    }
}

internal sealed partial class SteamLobbyManager
{
    private readonly LobbyListCallCapture _lobbyListCall = new();
    private readonly NativeLobbyListReader _lobbyListReader = new();
    private static SteamLobbyManager _listCallOwner;
    private readonly HarmonyLib.Harmony _listCallHarmony = new("CairnMultiplayerMod.LobbyList");
    private bool _listCallHookInstalled;
    private const int LobbyMatchListCallbackId = 510;

    // LobbyMatchList_t is a four-byte value type. Passing it through an IL2CPP
    // generic delegate corrupts the payload on this game build. Capture the exact
    // call through manual dispatch BEFORE Steamworks.NET drains it, with no
    // generic value-type delegate crossing the managed/native boundary.
    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamAPI_SteamMatchmaking_v009();
    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamAPI_SteamUtils_v010();
    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong SteamAPI_ISteamMatchmaking_RequestLobbyList(IntPtr self);
    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamUtils_IsAPICallCompleted(IntPtr self, ulong call,
        [MarshalAs(UnmanagedType.I1)] out bool failed);
    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ManualDispatch_GetAPICallResult(int pipe, ulong call,
        out uint count, int size, int callbackId, [MarshalAs(UnmanagedType.I1)] out bool failed);
    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SteamAPI_ManualDispatch_RunFrame(int pipe);

    private sealed class NativeLobbyListReader : ILobbyListResultReader
    {
        public void Advance() => SteamAPI_ManualDispatch_RunFrame(NativeSteamAPI_GetHSteamPipe());
        public bool IsCompleted(ulong call, out bool failed)
        {
            var utils = SteamAPI_SteamUtils_v010();
            if (utils == IntPtr.Zero) throw new InvalidOperationException("Steam utilities are unavailable.");
            return SteamAPI_ISteamUtils_IsAPICallCompleted(utils, call, out failed);
        }
        public bool Read(ulong call, out uint count, out bool failed)
            => SteamAPI_ManualDispatch_GetAPICallResult(NativeSteamAPI_GetHSteamPipe(), call,
                out count, sizeof(uint), LobbyMatchListCallbackId, out failed);
    }

    private static void BeforeSteamCallbackDispatch(bool __0)
    {
        // Game-server callbacks belong to a different pipe. Never read them.
        var owner = _listCallOwner;
        if (__0 || owner == null || owner._disposed) return;
        owner._lobbyListCall.Capture(owner._lobbyListReader);
    }

    private void StartLobbyListCall()
    {
        if (!_listCallHookInstalled)
        {
            _listCallHarmony.Patch(AccessTools.Method(typeof(CallbackDispatcher), nameof(CallbackDispatcher.RunFrame)),
                prefix: new HarmonyMethod(typeof(SteamLobbyManager), nameof(BeforeSteamCallbackDispatch)));
            _listCallHookInstalled = true;
        }
        var matchmaking = SteamAPI_SteamMatchmaking_v009();
        if (matchmaking == IntPtr.Zero)
            throw new InvalidOperationException("Steam matchmaking is unavailable.");
        var call = SteamAPI_ISteamMatchmaking_RequestLobbyList(matchmaking);
        if (call == 0)
            throw new InvalidOperationException("Steam could not start the lobby search.");
        _listCallOwner = this;
        _lobbyListCall.Begin(call);
    }

    private void PollLobbyListCall()
    {
        if (!_lobbyListCall.TryTake(out var count, out var error)) return;
        try
        {
            if (error != null) throw new InvalidOperationException(error);
            // Process outside the dispatch hook: completion can start a fallback
            // search, join a lobby, or complete an awaiting UI request.
            OnLobbyMatchListCb(count);
        }
        catch (Exception exception)
        {
            _lobbyListCall.Cancel();
            ModLog.Warning($"[SteamLobby] Lobby search failed: {exception.Message}");
            if (_joinTcs != null) FailJoin(exception.Message);
            var list = _listTcs;
            _listTcs = null;
            ClearOperationIfIdle();
            list?.TrySetResult(new List<LobbyEntry>());
            if (list != null) OnLobbyError?.Invoke(exception.Message);
        }
    }
}
