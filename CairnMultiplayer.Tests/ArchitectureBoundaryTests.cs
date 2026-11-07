#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class ArchitectureBoundaryTests
{
    private static readonly string ProjectDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CairnMultiplayerMod"));

    [Fact]
    public void FeaturesContainOnlyFeatureDeclarations()
    {
        var files = SourceFilesUnder("Features").ToArray();

        Assert.NotEmpty(files);
        Assert.DoesNotContain(files, file => !file.EndsWith("Feature.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void EngineInteropLivesOnlyInInternalOrBootstrap()
    {
        var violations = new List<string>();
        foreach (var file in SourceFilesUnder(string.Empty))
        {
            var relative = Path.GetRelativePath(ProjectDirectory, file);
            if (relative.StartsWith("Internal" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith("Bootstrap" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;

            var source = File.ReadAllText(file);
            if (ContainsEngineUsing(source)) violations.Add(relative);
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void GameApiContainsOnlySafeContractsAndFacades()
    {
        var violations = SourceFilesUnder("GameApi")
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return ContainsEngineUsing(source)
                       || source.Contains("CairnMultiplayerMod.Internal", StringComparison.Ordinal)
                       || source.Contains("Mod.Instance", StringComparison.Ordinal)
                       || source.Contains("Mod.Log", StringComparison.Ordinal);
            })
            .Select(file => Path.GetRelativePath(ProjectDirectory, file))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void InternalNamespaceExportsNoPublicTypes()
    {
        var publicType = new Regex(
            @"^\s*public\s+(?:(?:sealed|static|partial|abstract)\s+)*(?:class|interface|struct|enum|record)\s+",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var violations = SourceFilesUnder("Internal")
            .Where(file => publicType.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(ProjectDirectory, file))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void VoiceOrchestrationDependsOnThePlatformBackendBoundary()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Voice", "VoiceAdapter.cs"));

        Assert.Contains("VoiceAudioBackend", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsVoiceCapture", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsVoiceOutput", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PacketDispatcherDoesNotApplyGameOrTransportDetails()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Networking", "NetworkManager.PacketDispatch.cs"));

        Assert.DoesNotContain("using Unity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("using CairnMultiplayerMod.Internal.Game", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Deserialize(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkingDoesNotDependOnGameUiOrBootstrap()
    {
        var violations = SourceFilesUnder(Path.Combine("Internal", "Networking"))
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return source.Contains("CairnMultiplayerMod.Internal.Game", StringComparison.Ordinal)
                       || source.Contains("CairnMultiplayerMod.Internal.UI", StringComparison.Ordinal)
                       || source.Contains("CairnMultiplayerMod.Framework", StringComparison.Ordinal)
                       || source.Contains("CairnMultiplayerMod.GameApi", StringComparison.Ordinal)
                       || source.Contains("using Unity", StringComparison.Ordinal)
                       || Regex.IsMatch(source, @"\bMod\.", RegexOptions.CultureInvariant);
            })
            .Select(file => Path.GetRelativePath(ProjectDirectory, file))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void PublicManagedApiContainsNoEngineDependencies()
    {
        var violations = SourceFilesUnder("Api")
            .Where(file => ContainsEngineUsing(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(ProjectDirectory, file))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void DiagnosticsNeverTransmitDataOrCreateMachineIdentifiers()
    {
        var forbidden = new[]
        {
            "System.Net.Http",
            "System.Net.Sockets",
            "HttpClient",
            "WebClient",
            "WebRequest",
            "PostAsync",
            "/v1/crashes",
            "Environment.MachineName",
            "Environment.UserName",
        };
        var violations = SourceFilesUnder(Path.Combine("Internal", "Diagnostics"))
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return forbidden.Any(value => source.Contains(value, StringComparison.Ordinal));
            })
            .Select(file => Path.GetRelativePath(ProjectDirectory, file))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void RuntimeLayersDoNotReachThroughBootstrapSingletons()
    {
        var protectedDirectories = new[] { "Framework", "GameApi", "Internal" };
        var forbidden = new Regex(
            @"\bMod\.(?:Instance|Log|LogDebug)\b",
            RegexOptions.CultureInvariant);
        var violations = protectedDirectories
            .SelectMany(SourceFilesUnder)
            .Where(file => forbidden.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(ProjectDirectory, file))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void FrameworkDoesNotDependOnBootstrap()
    {
        var violations = SourceFilesUnder("Framework")
            .Where(file => File.ReadAllText(file).Contains(
                "CairnMultiplayerMod.Bootstrap", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(ProjectDirectory, file))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void EveryRegisteredGamePatchHasAnUninstallPath()
    {
        var registry = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "GamePatchRegistry.cs"));
        var modules = Regex.Matches(registry, @"new\([^\r\n]+?,\s*(\w+)\.Uninstall\)")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(modules);
        foreach (var module in modules)
        {
            var sources = SourceFilesUnder("Internal")
                .Select(File.ReadAllText)
                .Where(source => Regex.IsMatch(source, $@"\bclass\s+{Regex.Escape(module)}\b",
                    RegexOptions.CultureInvariant))
                .ToArray();

            Assert.Contains(sources, source => source.Contains($"class {module}", StringComparison.Ordinal));
            Assert.Contains(sources, source => source.Contains("void Uninstall(", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void CooperativeRopeStaysATetherAndLeavesProtectionToRealPitons()
    {
        var interop = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "RopeInterop.Belay.cs"));
        var patch = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "RopeTeamFallPatch.cs"));

        var pitons = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "RopeInterop.Pitons.cs"));
        var couple = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "RopeCoupleController.cs"));
        var chain = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "TeamAnchorChain.cs"));
        var session = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "TeamAnchorSession.cs"));

        // Cairn counts quickdraws on the Lifeline's securing rope as protection. Handing it
        // the cooperative rope, or answering IsSecured for it, showed "0 m" in red and left
        // secured falls looking for a piton that does not exist. An empty chain calls Cairn's
        // own unsecured fall. Nothing here pushes the pawn.
        Assert.DoesNotMatch(@"\.securingRope\s*=(?!=)", interop);
        Assert.DoesNotMatch(@"\.securingRope\s*=(?!=)", pitons);
        Assert.DoesNotMatch(@"\.securingRope\s*=(?!=)", chain);
        Assert.DoesNotMatch(@"\.securingRope\s*=(?!=)", session);
        Assert.DoesNotContain("typeof(Lifeline)", patch, StringComparison.Ordinal);
        Assert.Contains("TryGetInitialLength", interop, StringComparison.Ordinal);
        Assert.Contains("renderer.parts = _rope.ropeParts", interop, StringComparison.Ordinal);
        Assert.Contains("nameof(LogicalRopeRenderer.LateUpdate)", patch, StringComparison.Ordinal);
        AssertPartnerRopeLengthIsAppliedFromFixedUpdate(interop);
        Assert.Contains("SetHarnessVisual", interop, StringComparison.Ordinal);
        Assert.Contains("RestoreHarnessVisual", interop, StringComparison.Ordinal);
        Assert.Contains("MC_Outift_NoHarness", interop, StringComparison.Ordinal);
        Assert.Contains(
            "TeamAnchorSession.CopyPath",
            MethodBody(interop, "internal void DrawHarnessLine()"),
            StringComparison.Ordinal);
        Assert.Contains("ToFallingModuleUnsecure", pitons, StringComparison.Ordinal);

        var lengthPatch = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "RopeLengthPatch.cs"));
        var lengthPolicy = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "RopeLengthPolicy.cs"));
        Assert.DoesNotMatch(@"\.securingRope\s*=(?!=)", lengthPatch);
        Assert.DoesNotMatch(@"\.securingRope\s*=(?!=)", lengthPolicy);

        var lengthReadout = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "RopeLengthReadout.cs"));
        Assert.DoesNotMatch(@"\.securingRope\s*=(?!=)", lengthReadout);

        var staff = File.ReadAllText(Path.Combine(
                ProjectDirectory, "Internal", "Game", "Roping", "RopeInterop.TopAnchor.cs"))
            + File.ReadAllText(Path.Combine(
                ProjectDirectory, "Internal", "Game", "Roping", "TopAnchorPolicy.cs"))
            + File.ReadAllText(Path.Combine(
                ProjectDirectory, "Internal", "Game", "Roping", "TopAnchorSession.cs"))
            + File.ReadAllText(Path.Combine(
                ProjectDirectory, "Internal", "Game", "Roping", "TopAnchorPlant.cs"))
            + File.ReadAllText(Path.Combine(
                ProjectDirectory, "Internal", "Game", "Roping", "TopAnchorMessage.cs"));
        Assert.DoesNotMatch(@"\.securingRope\s*=(?!=)", staff);

        var heldPath = MethodBody(lengthPatch, "private static bool TryBuildHeldPath(");
        Assert.Contains("AppendChain", heldPath, StringComparison.Ordinal);
        Assert.DoesNotContain("partner", heldPath, StringComparison.Ordinal);

        var topAnchor = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "RopeInterop.TopAnchor.cs"));
        var tick = MethodBody(topAnchor, "private static void TickTopAnchor(");
        Assert.Contains("if (TeamAnchorSession.LeaderLeft)", tick, StringComparison.Ordinal);
        Assert.DoesNotContain("LeaderLeft && TeamAnchorSession.IsLocalFollower", tick, StringComparison.Ordinal);
        var heldDraw = MethodBody(topAnchor, "private static void DrawHeldLine(");
        Assert.Contains("TryGetHeldFollowerPosition", heldDraw, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetClimberPosition", heldDraw, StringComparison.Ordinal);

        var rope = interop + pitons + couple + chain + session + lengthPatch + lengthPolicy + lengthReadout + staff;
        Assert.DoesNotContain(".AddForce(", rope, StringComparison.Ordinal);
        Assert.DoesNotContain(".velocity", rope, StringComparison.Ordinal);
    }

    [Fact]
    public void TeamRopeStopsAtTheCoilThroughCairnsOwnCheck()
    {
        var patch = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "RopeLengthPatch.cs"));
        var registry = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "GamePatchRegistry.cs"));
        var draw = MethodBody(File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "RopeInterop.Belay.cs")),
            "internal void DrawHarnessLine()");

        Assert.Contains("nameof(Harness.LifelineCanGoToward)", patch, StringComparison.Ordinal);
        Assert.Contains("postfix:", patch, StringComparison.Ordinal);
        Assert.Contains("PawnStateType.Falling", patch, StringComparison.Ordinal);
        Assert.Contains("PawnStateType.Dead", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("ClampDist", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("HardValidateTargetBodyPosition", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("OnRopeForbidGoingToward", patch, StringComparison.Ordinal);
        Assert.Contains("RopeLengthPatch.Install", registry, StringComparison.Ordinal);
        Assert.Contains("RopeLengthPatch.Uninstall", registry, StringComparison.Ordinal);
        Assert.Contains("RopeLengthPolicy.IsPaidOut", draw, StringComparison.Ordinal);

        var sync = MethodBody(File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "RopeInterop.Pitons.cs")),
            "internal static void SyncPitons(");
        var bootstrap = File.ReadAllText(Path.Combine(ProjectDirectory, "Bootstrap", "Mod.cs"));
        Assert.Contains("RopeLengthReadout.Tick()", sync, StringComparison.Ordinal);
        Assert.Contains("RopeLengthReadout.Hide()", sync, StringComparison.Ordinal);
        Assert.Contains("RopeLengthReadout.Bind", bootstrap, StringComparison.Ordinal);
    }

    [Fact]
    public void ClimbotRopeStaysOff()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "ClimbotRopePatch.cs"));
        var registry = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "GamePatchRegistry.cs"));

        Assert.Contains("ShouldDisplayRope", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ForceHidden", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NetplayRemoteClimbot.Update", source, StringComparison.Ordinal);
        Assert.Contains("HideRemote", source, StringComparison.Ordinal);
        Assert.Contains("TeamAnchorSession.IsLocalLeader", source, StringComparison.Ordinal);
        Assert.Contains("TeamAnchorChain.KeepsClimbotCoil", source, StringComparison.Ordinal);
        Assert.Contains("TeamAnchorSession.LeaderLeft", source, StringComparison.Ordinal);
        Assert.Contains("HasSecuringRope", source, StringComparison.Ordinal);
        Assert.Contains("SetAttachedToHarness(true)", source, StringComparison.Ordinal);
        Assert.Contains("SetAttachedToHarness(false)", source, StringComparison.Ordinal);
        Assert.Contains("IsAttached", source, StringComparison.Ordinal);
        Assert.Contains("SetRopeActive(false, false)", source, StringComparison.Ordinal);
        Assert.Contains("SetVisible(false, false)", source, StringComparison.Ordinal);
        Assert.Contains("SaveStillLoading()", source, StringComparison.Ordinal);
        Assert.Contains("LoadingState.LoadingSave", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsLinked", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Object.Destroy", source, StringComparison.Ordinal);
        Assert.Contains("ClimbotRopePatch.Install", registry, StringComparison.Ordinal);
        Assert.Contains("ClimbotRopePatch.Uninstall", registry, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingClimbotCoroutineDoesNotStopTheSaveLoad()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "SavegameLoadPatch.cs"));
        var registry = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "GamePatchRegistry.cs"));
        var goToState = MethodBody(source, "private static Exception GoToStateFinalizer(");
        var read = MethodBody(source, "private static Exception ReadFromSavegameFinalizer(");

        Assert.Contains("!_readingSave", goToState, StringComparison.Ordinal);
        Assert.Contains("IsMissingStateCoroutine(__exception)", goToState, StringComparison.Ordinal);
        Assert.Contains("return __exception", goToState, StringComparison.Ordinal);
        Assert.Contains("return null", goToState, StringComparison.Ordinal);
        Assert.Contains("callback?.Invoke(true)", read, StringComparison.Ordinal);
        Assert.Contains("return __exception", read, StringComparison.Ordinal);
        Assert.Contains("SavegameLoadPatch.Install", registry, StringComparison.Ordinal);
        Assert.Contains("SavegameLoadPatch.Uninstall", registry, StringComparison.Ordinal);
    }

    [Fact]
    public void RopeTeamSaveGateRefusesAnythingOutsideCamp()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "CampSaveGatePatch.cs"));
        var registry = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "GamePatchRegistry.cs"));

        Assert.Contains("CannotSaveReason.PawnSaidNo", source, StringComparison.Ordinal);
        Assert.Contains("\"LoadInternal\"", source, StringComparison.Ordinal);
        Assert.Contains("success = false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke(true)", source, StringComparison.Ordinal);
        Assert.Contains("nameof(SavegameManager.AutoSave)", source, StringComparison.Ordinal);
        Assert.Contains("nameof(SavegameManager.QuickSave)", source, StringComparison.Ordinal);
        Assert.Contains("nameof(SavegameManager.SaveAndClose)", source, StringComparison.Ordinal);
        Assert.Contains("PawnControllerSwitcher.CanSave", source, StringComparison.Ordinal);
        Assert.Contains("CampSaveGatePatch.Install", registry, StringComparison.Ordinal);
        Assert.Contains("CampSaveGatePatch.Uninstall", registry, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTiedLeaderPassesCairnsRopeHeadCheck()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "TeamPitonPlacePatch.cs"));

        Assert.Contains("IsLocalClientRopeHead", source, StringComparison.Ordinal);
        Assert.Contains("nameof(ApplyLeaderRule)", source, StringComparison.Ordinal);
        Assert.Contains("postfix:", source, StringComparison.Ordinal);
        Assert.Contains("AllowsPlacement", source, StringComparison.Ordinal);
        Assert.Contains("NetplayNotRopeHead", source, StringComparison.Ordinal);
        Assert.Contains("PitonUnusabilityReason.Usable", source, StringComparison.Ordinal);
        Assert.Contains("LeaderLeft", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RefuseWhenFollower", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReverseEngineeredInteropUsesGeneratedApisInsteadOfManualIl2CppLayouts()
    {
        var localPlayer = ReadGameSource("Players", "LocalPlayerInterop.cs");
        var mainMenu = ReadGameSource("MainMenu", "MainMenuInterop.cs");
        var time = ReadGameSource("TimeInterop.cs");
        var lamp = ReadGameSource("Players", "LampInterop.cs");
        var setFrame = ReadGameSource("Players", "NetplaySetFramePatch.cs");
        var pitons = ReadGameSource("Roping", "RopeInterop.Pitons.cs");

        Assert.Contains("PawnManager.Instance?.MCGameObject", localPlayer, StringComparison.Ordinal);
        Assert.Contains("NativeMainMenu.ForceStepTransition", mainMenu, StringComparison.Ordinal);
        Assert.Contains("PlayerStateFeedbacks.Instance", time, StringComparison.Ordinal);
        Assert.Contains("lamp.SetMode(", lamp, StringComparison.Ordinal);
        Assert.Contains("instance.currentFrame = frame", setFrame, StringComparison.Ordinal);
        Assert.Contains("lifeline.AddPiton(", pitons, StringComparison.Ordinal);
        Assert.Contains("lifeline.DetachPiton(", pitons, StringComparison.Ordinal);
        Assert.Contains("PlacedPitons", pitons, StringComparison.Ordinal);

        var combined = string.Concat(localPlayer, mainMenu, time, lamp, setFrame, pitons);
        Assert.DoesNotContain("GetIl2CppField", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("il2cpp_field_get_offset", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("il2cpp_runtime_invoke", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteWeatherOverrideUsesCairnsNativeCleanupLifecycle()
    {
        var weather = ReadGameSource("WeatherInterop.cs");

        Assert.Contains("forcedInifiniteStateDefOrigin", weather, StringComparison.Ordinal);
        Assert.Contains("ForceInfiniteWeatherState(\n                null", weather, StringComparison.Ordinal);
        Assert.Contains("DefineWeatherDefinitionsArray()", weather, StringComparison.Ordinal);
        Assert.Contains("TriggerCurrentWeatherState()", weather, StringComparison.Ordinal);
        Assert.Contains("WindOverride.NoOverride", weather, StringComparison.Ordinal);
    }

    private static IEnumerable<string> SourceFilesUnder(string relativeDirectory)
    {
        var directory = Path.Combine(ProjectDirectory, relativeDirectory);
        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                           && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));
    }

    private static string ReadGameSource(params string[] relativePath)
        => File.ReadAllText(relativePath.Aggregate(
            Path.Combine(ProjectDirectory, "Internal", "Game"), Path.Combine));

    /// <summary>
    /// The cloned rope part keeps the prefab's full length. Setting that length from
    /// Attach runs during Update, which Cairn rejects, so the line stays at the horizon.
    /// </summary>
    private static void AssertPartnerRopeLengthIsAppliedFromFixedUpdate(string interop)
    {
        var attach = MethodBody(interop, "public bool Attach()");
        var snap = MethodBody(interop, "internal void ApplyPendingSnap()");
        var finish = MethodBody(interop, "internal void FinishSnapAfterPhysics()");
        var beforePhysics = MethodBody(interop, "internal static bool BeforeRopePhysics(");

        Assert.DoesNotContain("_rope.RequestSetLength", attach, StringComparison.Ordinal);
        Assert.DoesNotContain("_rope.SetLengthInstant", attach, StringComparison.Ordinal);
        Assert.DoesNotContain("_rope.TeleportOnAttachPoints", attach, StringComparison.Ordinal);
        Assert.Contains("_pendingSnapLength = initialLength", attach, StringComparison.Ordinal);
        Assert.Contains("SetRopeRendered(false)", attach, StringComparison.Ordinal);

        Assert.Contains("SetLengthInstant(length)", snap, StringComparison.Ordinal);
        Assert.Contains("RequestSetLength(length, false, true)", snap, StringComparison.Ordinal);
        Assert.Contains(
            "TeleportOnAttachPoints(RopeTeleportMode.PreTeleportOnLineAndFabrik, false, false)",
            snap, StringComparison.Ordinal);
        Assert.Contains("RopeAttachmentPolicy.SnapLanded(length, reported)", finish, StringComparison.Ordinal);
        Assert.Contains("LayParticlesOnSegment()", finish, StringComparison.Ordinal);
        Assert.Contains("DrawHarnessLine()", finish, StringComparison.Ordinal);
        Assert.Contains("DrawHarnessLine()", MethodBody(interop, "public bool Maintain()"), StringComparison.Ordinal);
        Assert.DoesNotContain("SetRopeRendered(true)", interop, StringComparison.Ordinal);
        Assert.Contains("ApplyPendingSnap()", beforePhysics, StringComparison.Ordinal);
        // Skipping FixedUpdate while the session is still waiting aborts ForceInitialize
        // on a retie, and the cloned part keeps the prefab length.
        Assert.Contains("if (!IsNativeBelayEngaged) return true;", beforePhysics, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseAllAnchors()", beforePhysics, StringComparison.Ordinal);
    }

    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(start, i - start + 1);
            }
        }

        throw new InvalidOperationException(signature);
    }

    private static bool ContainsEngineUsing(string source)
    {
        return source.Contains("using Unity", StringComparison.Ordinal)
               || source.Contains("using Il2Cpp", StringComparison.Ordinal)
               || source.Contains("using HarmonyLib", StringComparison.Ordinal)
               || source.Contains("using MelonLoader", StringComparison.Ordinal)
               || source.Contains("using Steamworks", StringComparison.Ordinal);
    }
}
