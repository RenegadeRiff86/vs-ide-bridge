using System.IO.Pipes;
#if NET8_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace VsIdeBridge.Shared;

#if NET8_0_OR_GREATER
[SupportedOSPlatform("windows")]
#endif
public static class NamedPipeAccessDefaults
{
    // Windows named-pipe clients need Synchronize in addition to read/write rights.
    public const PipeAccessRights ClientReadWriteRights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
}