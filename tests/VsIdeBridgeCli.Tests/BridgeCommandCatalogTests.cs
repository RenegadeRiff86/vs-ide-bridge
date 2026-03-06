using VsIdeBridge.Shared;
using Xunit;

namespace VsIdeBridgeCli.Tests;

public sealed class BridgeCommandCatalogTests
{
    [Fact]
    public void CatalogMetadata_Validate_ReturnsNoErrors()
    {
        var errors = BridgeCommandCatalog.Validate().ToArray();
        Assert.Empty(errors);
    }

    [Fact]
    public void CatalogMetadata_IncludesExpectedPhaseOneCommands()
    {
        string[] requiredPipeNames =
        [
            "ready",
            "count-references",
            "debug-threads",
            "debug-stack",
            "debug-locals",
            "debug-modules",
            "debug-watch",
            "debug-exceptions",
            "diagnostics-snapshot",
            "build-configurations",
            "set-build-configuration",
            "find-files",
            "open-document",
            "save-document",
        ];

        foreach (var pipeName in requiredPipeNames)
        {
            Assert.True(
                BridgeCommandCatalog.TryGetByPipeName(pipeName, out _),
                $"Expected bridge metadata entry for command '{pipeName}'.");
        }
    }
}
