using System;
using System.Runtime.Serialization;

namespace VsIdeBridge.Infrastructure;

[Serializable]
internal sealed class CommandErrorException : Exception
{
    private const string DefaultCode = "internal_error";

    public CommandErrorException()
        : this(DefaultCode, "Command failed.")
    {
    }

    public CommandErrorException(string message)
        : this(DefaultCode, message)
    {
    }

    public CommandErrorException(string message, Exception innerException)
        : this(DefaultCode, message, innerException)
    {
    }

    public CommandErrorException(string code, string message, object? details = null)
        : base(message)
    {
        Code = string.IsNullOrWhiteSpace(code) ? DefaultCode : code;
        Details = details;
    }

    public CommandErrorException(string code, string message, Exception innerException, object? details = null)
        : base(message, innerException)
    {
        Code = string.IsNullOrWhiteSpace(code) ? DefaultCode : code;
        Details = details;
    }

    private CommandErrorException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        Code = info.GetString(nameof(Code)) ?? DefaultCode;
        Details = info.GetValue(nameof(Details), typeof(object));
    }

    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        if (info is null)
        {
            throw new ArgumentNullException(nameof(info));
        }

        info.AddValue(nameof(Code), Code);
        info.AddValue(nameof(Details), Details);
        base.GetObjectData(info, context);
    }

    public string Code { get; }

    public object? Details { get; }
}
