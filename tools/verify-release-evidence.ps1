[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $RemainingArguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$verifierPath = Join-Path $PSScriptRoot 'verify-release-evidence.py'
if (-not (Test-Path -LiteralPath $verifierPath -PathType Leaf)) {
    throw "Release evidence verifier '$verifierPath' was not found."
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
    throw 'Python 3 is required to verify release evidence.'
}

$arguments = @(
    $pythonPrefix
    $verifierPath
    '--repository-root'
    $repositoryRoot
) + $RemainingArguments

& $pythonCommand @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Release evidence verification failed with exit code $LASTEXITCODE."
}
