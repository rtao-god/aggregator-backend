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
        'compose-config',
        'compose-build',
        'db-migrate',
        'compose-up',
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
$composeFile = Join-Path $root 'compose.yaml'
$composeEnvironment = Join-Path $root '.env'
$composeEnvironmentExample = Join-Path $root '.env.example'

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

    throw 'Python 3 is required for repository validation.'
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

function Resolve-ComposeEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$AllowExample
    )

    if (Test-Path -LiteralPath $composeEnvironment) {
        $content = Get-Content -LiteralPath $composeEnvironment -Raw
        if ($content -match '(?m)=CHANGE_ME') {
            throw "'$composeEnvironment' still contains CHANGE_ME values."
        }

        return $composeEnvironment
    }

    if ($AllowExample) {
        return $composeEnvironmentExample
    }

    throw "Create '$composeEnvironment' from '.env.example' and replace every CHANGE_ME value."
}

function Invoke-InventoryCheck {
    $python = Resolve-Python
    Invoke-Native $python @('.tools/complete-backend.py', '--check')
}

function Invoke-ContractCheck {
    $python = Resolve-Python
    Invoke-Native $python @('tools/verify-contracts.py')
}

function Invoke-Compose {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [bool]$AllowExample
    )

    $environmentFile = Resolve-ComposeEnvironment -AllowExample $AllowExample
    $composeArguments = @(
        'compose',
        '--env-file',
        $environmentFile,
        '--file',
        $composeFile) + $Arguments
    Invoke-Native docker $composeArguments
}

function Invoke-ComposeConfig {
    Invoke-Compose -AllowExample $true -Arguments @('config', '--quiet')
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
    Invoke-ContractCheck
    Invoke-ComposeConfig
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
        'compose-config' {
            Invoke-ComposeConfig
        }
        'compose-build' {
            Invoke-ComposeConfig
            Invoke-Compose -AllowExample $true -Arguments @('build')
        }
        'db-migrate' {
            Invoke-ComposeConfig
            Invoke-Compose -AllowExample $false -Arguments @(
                'up',
                '--no-build',
                '--wait',
                'catalog-grants',
                'query-grants',
                'ingestion-grants',
                'analytics-grants',
                'promotion-grants')
        }
        'compose-up' {
            Invoke-ComposeConfig
            Invoke-Compose -AllowExample $false -Arguments @(
                'up',
                '--detach',
                '--wait',
                '--no-build')
        }
        'compose-down' {
            Invoke-Compose -AllowExample $true -Arguments @(
                'down',
                '--remove-orphans')
        }
    }
}
finally {
    Pop-Location
}
