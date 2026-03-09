using System;

namespace VsIdeBridge.Shared;

internal enum AddFilePatchDecision
{
    Create,
    AlreadySatisfied,
    Conflict,
}

internal static class AddFilePatchSemantics
{
    public static AddFilePatchDecision Evaluate(string desiredContent, string? existingContent)
    {
        if (existingContent is null)
        {
            return AddFilePatchDecision.Create;
        }

        return string.Equals(existingContent, desiredContent, StringComparison.Ordinal)
            ? AddFilePatchDecision.AlreadySatisfied
            : AddFilePatchDecision.Conflict;
    }
}
