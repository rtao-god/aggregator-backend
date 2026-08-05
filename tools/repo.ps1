[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet(
        'preflight',
        'restore',
        'build-project',
        'build-runtime',
        'build',
        'test-project',
        'test-architecture',
        'test-all',
        'format-check',
        'format-full-check',
        'compose-build',
        'compose-up',
        'compose-up-runtime',
        'compose-down')]
    [string]$Command,

    [Parameter()]
    [string]$Project
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'AggregatorBackend.slnx'
$runtimeSolution = Join-Path $root 'AggregatorBackend.Runtime.slnx'
$architectureProject = Join-Path $root 'tests/Architecture.Tests/Architecture.Tests.csproj'

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:NUGET_XMLDOC_MODE = 'skip'

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable failed with exit code $LASTEXITCODE."
    }
}

function Resolve-Python {
    foreach ($candidate in @('python3', 'python')) {
        if (Get-Command $candidate -ErrorAction SilentlyContinue) {
            return $candidate
        }
    }

    throw 'Python 3 is required for repository inventory validation.'
}

function Resolve-Project {
    if ([string]::IsNullOrWhiteSpace($Project)) {
        throw "Command '$Command' requires -Project <relative-or-absolute.csproj>."
    }

    $candidate = if ([System.IO.Path]::IsPathRooted($Project)) {
        $Project
    }
    else {
        Join-Path $root $Project
    }

    $resolved = (Resolve-Path -LiteralPath $candidate).Path
    $rootPrefix = $root.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar

    if (-not $resolved.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetExtension($resolved) -ne '.csproj') {
        throw "Project must be an existing .csproj inside '$root'."
    }

    return $resolved
}

function Invoke-InventoryCheck {
    $python = Resolve-Python
    Invoke-Native $python @('.tools/complete-backend.py', '--check')
}

function Invoke-ArchitectureGate {
    Invoke-Native dotnet @('restore', $architectureProject)
    Invoke-Native dotnet @(
        'test',
        $architectureProject,
        '--no-restore',
        '-warnaserror',
        '--logger',
        'console;verbosity=normal',
        '/m:1',
        '/nr:false')
}

function Invoke-Preflight {
    Invoke-InventoryCheck
    Invoke-ArchitectureGate
}

function Invoke-RuntimeBuild {
    Invoke-Native dotnet @('restore', $runtimeSolution)
    Invoke-Native dotnet @(
        'build',
        $runtimeSolution,
        '--no-restore',
        '-warnaserror',
        '/m:2',
        '/nr:false')
}

Push-Location $root
try {
    switch ($Command) {
        'preflight' {
            Invoke-Preflight
        }
        'restore' {
            Invoke-Native dotnet @('restore', $solution)
        }
        'build-project' {
            $target = Resolve-Project
            Invoke-Native dotnet @('build', $target, '-warnaserror', '/m:2', '/nr:false')
        }
        'build-runtime' {
            Invoke-RuntimeBuild
        }
        'build' {
            Invoke-Native dotnet @(
                'build',
                $solution,
                '-warnaserror',
                '/m:2',
                '/nr:false')
        }
        'test-project' {
            $target = Resolve-Project
            Invoke-Native dotnet @(
                'test',
                $target,
                '-warnaserror',
                '--logger',
                'console;verbosity=normal',
                '/m:1',
                '/nr:false')
        }
        'test-architecture' {
            Invoke-ArchitectureGate
        }
        'test-all' {
            Invoke-Preflight
            Invoke-RuntimeBuild
            Invoke-Native dotnet @('restore', $solution)
            Invoke-Native dotnet @(
                'build',
                $solution,
                '--no-restore',
                '-warnaserror',
                '/m:2',
                '/nr:false')
            Invoke-Native dotnet @(
                'test',
                $solution,
                '--no-build',
                '--no-restore',
                '-warnaserror',
                '--logger',
                'console;verbosity=normal',
                '/m:1',
                '/nr:false')
        }
        'format-check' {
            Invoke-Native dotnet @(
                'format',
                'whitespace',
                $solution,
                '--verify-no-changes',
                '--no-restore')
        }
        'format-full-check' {
            Invoke-Native dotnet @(
                'format',
                $solution,
                '--verify-no-changes',
                '--no-restore')
        }
        'compose-build' {
            Invoke-Native docker @(
                'compose',
                '-f',
                'compose.yml',
                '-f',
                'compose.runtime.yml',
                'build',
                '--parallel')
        }
        'compose-up' {
            Invoke-Native docker @(
                'compose',
                '-f',
                'compose.yml',
                'up',
                '--detach',
                '--wait')
        }
        'compose-up-runtime' {
            Invoke-Native docker @(
                'compose',
                '-f',
                'compose.yml',
                '-f',
                'compose.runtime.yml',
                'up',
                '--detach',
                '--wait')
        }
        'compose-down' {
            Invoke-Native docker @(
                'compose',
                '-f',
                'compose.yml',
                '-f',
                'compose.runtime.yml',
                'down',
                '--remove-orphans')
        }
    }
}
finally {
    Pop-Location
}
