[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $RemainingArguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$runnerPath = Join-Path $PSScriptRoot 'run-source-verification-proof.py'
if (-not (Test-Path -LiteralPath $runnerPath -PathType Leaf)) {
    throw "Source verification proof runner '$runnerPath' was not found."
}

$pythonCommand = $null
$pythonPrefix = @()
if (Get-Command py -ErrorAction SilentlyContinue) {
    $pythonCommand = 'py'
    $pythonPrefix = @('-3')
}
elseif (Get-Command python -ErrorAction SilentlyContinue) {
    $pythonCommand = 'python'
}
else {
    throw 'Python 3 is required to execute the source verification proof.'
}

$arguments = @(
    $pythonPrefix
    $runnerPath
    '--repository-root'
    $repositoryRoot
) + $RemainingArguments

& $pythonCommand @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Source verification proof failed with exit code $LASTEXITCODE."
}
