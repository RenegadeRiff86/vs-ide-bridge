using System;

namespace VsIdeBridge.Infrastructure;

internal sealed class CommandErrorException : Exception
{
    public CommandErrorException(string code, string message, object? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }

    public object? Details { get; }
}
