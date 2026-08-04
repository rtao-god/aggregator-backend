[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('restore', 'build', 'test', 'architecture', 'format-check', 'compose-up', 'compose-down')]
    [string]$Command
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'AggregatorBackend.slnx'

Push-Location $root
try {
    switch ($Command) {
        'restore' {
            dotnet restore $solution
        }
        'build' {
            dotnet build $solution --no-restore -warnaserror
        }
        'test' {
            dotnet test $solution --no-build --logger 'console;verbosity=normal'
        }
        'architecture' {
            dotnet test './tests/Architecture.Tests/Architecture.Tests.csproj' --no-build --logger 'console;verbosity=normal'
        }
        'format-check' {
            dotnet format $solution --verify-no-changes --no-restore
        }
        'compose-up' {
            docker compose --profile core up --build --detach
        }
        'compose-down' {
            docker compose --profile core down --remove-orphans
        }
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Command '$Command' failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
