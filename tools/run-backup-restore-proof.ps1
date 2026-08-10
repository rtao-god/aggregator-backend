[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $RemainingArguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$runnerPath = Join-Path $PSScriptRoot 'run-backup-restore-proof.py'
if (-not (Test-Path -LiteralPath $runnerPath -PathType Leaf)) {
    throw "Backup/restore proof runner '$runnerPath' was not found."
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
    throw 'Python 3 is required to execute the backup/restore proof.'
}

$arguments = @(
    $pythonPrefix
    $runnerPath
    '--repository-root'
    $repositoryRoot
) + $RemainingArguments

& $pythonCommand @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Backup/restore proof failed with exit code $LASTEXITCODE."
}
