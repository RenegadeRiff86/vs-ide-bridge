using System;

namespace VsIdeBridge.Infrastructure;

internal sealed class CommandErrorException(string code, string message, object? details = null) : Exception(message)
{
    public string Code { get; } = code;

    public object? Details { get; } = details;
}
