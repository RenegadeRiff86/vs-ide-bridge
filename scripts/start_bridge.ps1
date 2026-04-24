param(
    [Parameter(Mandatory = $true)]
    [string]$SolutionPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$solutionFullPath = [System.IO.Path]::GetFullPath($SolutionPath)
if (-not (Test-Path -LiteralPath $solutionFullPath)) {
    throw "Solution not found: $solutionFullPath"
}

throw @"
scripts\start_bridge.ps1 no longer launches the removed VsIdeBridgeCli workflow.

VS IDE Bridge is MCP-service based now:
- open the solution in Visual Studio
- make sure the installed VsIdeBridgeService is running
- connect your MCP client to C:\Program Files\VsIdeBridge\service\VsIdeBridgeService.exe with the mcp-server argument

See README.md for the current MCP setup flow.
"@
