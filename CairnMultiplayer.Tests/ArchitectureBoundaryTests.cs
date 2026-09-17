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
    public void CooperativeRopeSelectsNativeBelayButScopesPersonalPitonOperations()
    {
        var interop = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "RopeInterop.Belay.cs"));
        var patch = File.ReadAllText(Path.Combine(
            ProjectDirectory, "Internal", "Game", "Roping", "RopeTeamFallPatch.cs"));

        Assert.Contains("_lifeline.securingRope = _rope", interop, StringComparison.Ordinal);
        Assert.Contains("_lifeline.securingRope = _personalRope", interop, StringComparison.Ordinal);
        Assert.Contains("TryGetInitialLength", interop, StringComparison.Ordinal);
        Assert.Contains("renderer.parts = _rope.ropeParts", interop, StringComparison.Ordinal);
        Assert.Contains("SetHarnessVisual", interop, StringComparison.Ordinal);
        Assert.Contains("RestoreHarnessVisual", interop, StringComparison.Ordinal);
        Assert.Contains("MC_Outift_NoHarness", interop, StringComparison.Ordinal);
        Assert.Contains("BeginPersonalRopeOperation", patch, StringComparison.Ordinal);
        Assert.Contains("AfterPersonalOperation", patch, StringComparison.Ordinal);
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

    private static bool ContainsEngineUsing(string source)
    {
        return source.Contains("using Unity", StringComparison.Ordinal)
               || source.Contains("using Il2Cpp", StringComparison.Ordinal)
               || source.Contains("using HarmonyLib", StringComparison.Ordinal)
               || source.Contains("using MelonLoader", StringComparison.Ordinal)
               || source.Contains("using Steamworks", StringComparison.Ordinal);
    }
}
