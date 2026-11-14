#nullable enable

using System.IO;
using Xunit;

namespace CairnMultiplayerShared.Tests;

public class SaveCompatibilityTests
{
    [Fact]
    public void SaveSourcesDoNotPersistNativePitonReports()
    {
        var root = FindRepoRoot();
        var roping = Path.Combine(root, "CairnMultiplayerMod", "Internal", "Game", "Roping");
        foreach (var name in new[]
                 {
                     "BivouacSaveGatePatch.cs",
                     "BivouacSavePolicy.cs",
                     "BivouacSaveSession.cs",
                     "SavegamePitonGuardPatch.cs",
                 })
        {
            var source = File.ReadAllText(Path.Combine(roping, name));
            Assert.DoesNotContain("NativePiton", source);
            Assert.DoesNotContain("RopeClipRefusal", source);
            Assert.DoesNotContain("HoldsPiton", source);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "versions.json")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("versions.json");
    }
}
