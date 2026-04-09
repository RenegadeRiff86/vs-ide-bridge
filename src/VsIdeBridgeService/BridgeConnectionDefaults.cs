namespace VsIdeBridgeService;

internal static class BridgeConnectionDefaults
{
    public const int FastTimeoutMs = 15_000;
    public const int InteractiveTimeoutMs = 45_000;
    public const int HeavyTimeoutMs = 130_000;
    public const int FastPipeGateTimeoutMs = 750;
    public const int InteractivePipeGateTimeoutMs = 2_000;
    public const int HeavyPipeGateTimeoutMs = 5_000;
    public const int BridgeError = -32001;
    public const int TimeoutError = -32002;
    public const int CommError = -32003;
    public const int UnboundError = -32004;
}
