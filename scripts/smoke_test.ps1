param(
    [string]$SolutionPath,

    [switch]$CloseVisualStudio
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($SolutionPath)) {
    $SolutionPath = Join-Path $repoRoot "VsIdeBridge.sln"
}

$outputDir = Join-Path $repoRoot "output"
[System.IO.Directory]::CreateDirectory($outputDir) | Out-Null

$readyPath = Join-Path $outputDir "smoke-ready.json"
$statePath = Join-Path $outputDir "smoke-state.json"
$filesPath = Join-Path $outputDir "smoke-find-files.json"
$textPath = Join-Path $outputDir "smoke-find-text.json"
$reportPath = Join-Path $outputDir "smoke-test.txt"

$invokeScript = Join-Path $PSScriptRoot "invoke_vs_ide_command.ps1"

& $invokeScript -SolutionPath $SolutionPath -CommandName "Tools.IdeWaitForReady" -CommandArgs "--timeout-ms 120000" -OutputPath $readyPath -ReuseVisualStudio $true
& $invokeScript -SolutionPath $SolutionPath -CommandName "Tools.IdeGetState" -OutputPath $statePath -ReuseVisualStudio $true
& $invokeScript -SolutionPath $SolutionPath -CommandName "Tools.IdeFindFiles" -CommandArgs "--query ""IdeCoreCommands.cs""" -OutputPath $filesPath -ReuseVisualStudio $true
& $invokeScript -SolutionPath $SolutionPath -CommandName "Tools.IdeFindText" -CommandArgs "--query ""Tools.IdeGetState"" --scope solution" -OutputPath $textPath -ReuseVisualStudio $true -CloseVisualStudio:$CloseVisualStudio.IsPresent

$ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
$files = Get-Content -LiteralPath $filesPath -Raw | ConvertFrom-Json
$text = Get-Content -LiteralPath $textPath -Raw | ConvertFrom-Json

$report = @(
    "VS IDE Bridge Smoke Test"
    "TimestampUtc=$([DateTime]::UtcNow.ToString("o"))"
    "SolutionPath=$SolutionPath"
    "ReadySuccess=$($ready.success)"
    "StateSuccess=$($state.success)"
    "FindFilesSuccess=$($files.success)"
    "FindTextSuccess=$($text.success)"
    "MatchedFiles=$($files.data.count)"
    "MatchedTextRows=$($text.data.count)"
    "ActiveSolution=$($state.data.solutionPath)"
    "ActiveDocument=$($state.data.activeDocument)"
    "ReadyOutput=$readyPath"
    "StateOutput=$statePath"
    "FindFilesOutput=$filesPath"
    "FindTextOutput=$textPath"
)

Set-Content -LiteralPath $reportPath -Value $report -Encoding UTF8
Write-Host "Smoke test report: $reportPath"
