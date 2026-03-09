using VsIdeBridge.Shared;
using Xunit;

namespace VsIdeBridgeCli.Tests;

public sealed class AddFilePatchSemanticsTests
{
    [Fact]
    public void Evaluate_ReturnsCreate_WhenTargetDoesNotExist()
    {
        var decision = AddFilePatchSemantics.Evaluate("bridge content\n", existingContent: null);

        Assert.Equal(AddFilePatchDecision.Create, decision);
    }

    [Fact]
    public void Evaluate_ReturnsAlreadySatisfied_WhenExistingContentMatches()
    {
        const string DesiredContent = "bridge content\nline two\n";

        var decision = AddFilePatchSemantics.Evaluate(DesiredContent, DesiredContent);

        Assert.Equal(AddFilePatchDecision.AlreadySatisfied, decision);
    }

    [Fact]
    public void Evaluate_ReturnsConflict_WhenExistingContentDiffers()
    {
        var decision = AddFilePatchSemantics.Evaluate("bridge content\n", "other content\n");

        Assert.Equal(AddFilePatchDecision.Conflict, decision);
    }
}
