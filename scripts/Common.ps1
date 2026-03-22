function Write-Log {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message,

        [ValidateSet("TRACE", "INFO", "WARN", "ERROR")]
        [string]$Level = "INFO"
    )

    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    Write-Host "[$timestamp] [$Level] $Message"
}

function Write-Section {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Title
    )

    Write-Host ""
    Write-Log -Message $Title
}

function Write-FunctionTrace {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [hashtable]$Arguments = @{}
    )

    if ($Arguments.Count -eq 0) {
        Write-Log -Message "Entering $Name" -Level "TRACE"
        return
    }

    $pairs = $Arguments.GetEnumerator() |
        Sort-Object Key |
        ForEach-Object { "$($_.Key)=$($_.Value)" }
    Write-Log -Message "Entering $Name ($($pairs -join ', '))" -Level "TRACE"
}

function Format-CommandArgument {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    if ($Value -match '\s|"') {
        return '"' + $Value.Replace('"', '\"') + '"'
    }

    return $Value
}

function Format-CommandLine {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $formattedArguments = $Arguments | ForEach-Object { Format-CommandArgument -Value $_ }
    return "$Executable $($formattedArguments -join ' ')"
}

function Resolve-DisplayPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Get-RelativeDisplayPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BasePath,

        [Parameter(Mandatory = $true)]
        [string]$TargetPath
    )

    $resolvedBasePath = Resolve-DisplayPath -Path $BasePath
    $resolvedTargetPath = Resolve-DisplayPath -Path $TargetPath

    if (-not $resolvedBasePath.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $resolvedBasePath += [System.IO.Path]::DirectorySeparatorChar
    }

    $baseUri = [Uri]$resolvedBasePath
    $targetUri = [Uri]$resolvedTargetPath
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('\', '/')
}

function Write-PathTrace {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Label,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $resolvedPath = Resolve-DisplayPath -Path $Path
    $exists = Test-Path $resolvedPath
    Write-Log -Message "${Label}: $resolvedPath (exists=$exists)" -Level "TRACE"
}

function Write-ParameterTrace {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Values
    )

    foreach ($entry in $Values.GetEnumerator() | Sort-Object Key) {
        Write-Log -Message "Parameter $($entry.Key) = $($entry.Value)" -Level "TRACE"
    }
}

function Get-ProjectReferencesRecursive {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath,

        [Parameter(Mandatory = $true)]
        [hashtable]$Visited
    )

    $resolvedProjectPath = Resolve-DisplayPath -Path $ProjectPath
    if ($Visited.ContainsKey($resolvedProjectPath)) {
        return
    }

    $Visited[$resolvedProjectPath] = $true

    if (-not (Test-Path $resolvedProjectPath)) {
        return
    }

    [xml]$projectXml = Get-Content $resolvedProjectPath
    $projectDirectory = Split-Path -Parent $resolvedProjectPath
    $projectReferences = @($projectXml.SelectNodes('//ProjectReference'))

    foreach ($projectReference in $projectReferences) {
        $includePath = $projectReference.Include
        if ([string]::IsNullOrWhiteSpace($includePath)) {
            continue
        }

        $referencePath = Resolve-DisplayPath -Path (Join-Path $projectDirectory $includePath)
        Get-ProjectReferencesRecursive -ProjectPath $referencePath -Visited $Visited
    }
}

function Write-ProjectTrace {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $visited = @{}
    Get-ProjectReferencesRecursive -ProjectPath $ProjectPath -Visited $visited
    $resolvedProjectPath = Resolve-DisplayPath -Path $ProjectPath

    Write-Log -Message "$Description project graph:" -Level "TRACE"
    foreach ($path in $visited.Keys | Sort-Object) {
        $prefix = if ($path -eq $resolvedProjectPath) { "*" } else { "-" }
        Write-Log -Message "$prefix $path" -Level "TRACE"
    }
}

function Invoke-DotnetCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Description,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    $commandLine = Format-CommandLine -Executable "dotnet" -Arguments $Arguments
    Write-Log -Message "$Description" -Level "INFO"
    Write-Log -Message "Running: $commandLine" -Level "TRACE"
    $exitCode = Invoke-DotnetCommandWithPolling -Arguments $Arguments
    if ($exitCode -ne 0) {
        if (Test-IsBuildLikeDotnetCommand -Arguments $Arguments) {
            $diagnosticPaths = New-DotnetDiagnosticPaths -Description $Description
            $diagnosticArguments = Get-DiagnosticDotnetArguments -Arguments $Arguments
            $diagnosticCommandLine = Format-CommandLine -Executable "dotnet" -Arguments $diagnosticArguments

            Write-Log -Message "Build failed; rerunning with diagnostic verbosity." -Level "WARN"
            Write-Log -Message "Diagnostic log: $($diagnosticPaths.TextLogPath)" -Level "WARN"
            Write-Log -Message "Running: $diagnosticCommandLine" -Level "TRACE"

            Invoke-DotnetCommandWithPolling -Arguments $diagnosticArguments -LogPath $diagnosticPaths.TextLogPath | Out-Null
            throw "$FailureMessage See '$($diagnosticPaths.TextLogPath)'."
        }

        throw $FailureMessage
    }
}

function Invoke-DotnetCommandWithPolling {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [string]$LogPath
    )

    # Windows PowerShell 5.1 plus this shell interface buffers native child output poorly. Run dotnet in a
    # background job and poll the emitted records so long-running workflows surface progress incrementally.
    $job = Start-Job -ScriptBlock {
        param([string[]]$ForwardedArguments)

        & dotnet @ForwardedArguments 2>&1 | ForEach-Object {
            [pscustomobject]@{
                kind = 'output'
                text = $_.ToString()
            }
        }

        [pscustomobject]@{
            kind = 'exit'
            code = $LASTEXITCODE
        }
    } -ArgumentList (, $Arguments)

    $exitCode = $null
    $writer = $null
    if ($LogPath) {
        Ensure-ParentDirectory -Path $LogPath
        $writer = [System.IO.StreamWriter]::new((Resolve-DisplayPath -Path $LogPath), $false, [System.Text.Encoding]::UTF8)
        $writer.AutoFlush = $true
    }

    try {
        while ($true) {
            foreach ($item in (Receive-Job -Job $job)) {
                if ($item.kind -eq 'output') {
                    Write-Host $item.text
                    if ($writer) {
                        $writer.WriteLine($item.text)
                    }
                }
                elseif ($item.kind -eq 'exit') {
                    $exitCode = [int]$item.code
                }
            }

            if ($job.State -in @('Completed', 'Failed', 'Stopped')) {
                break
            }

            Start-Sleep -Milliseconds 250
        }

        foreach ($item in (Receive-Job -Job $job)) {
            if ($item.kind -eq 'output') {
                Write-Host $item.text
                if ($writer) {
                    $writer.WriteLine($item.text)
                }
            }
            elseif ($item.kind -eq 'exit') {
                $exitCode = [int]$item.code
            }
        }
    }
    finally {
        if ($writer) {
            $writer.Dispose()
        }

        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    }

    if ($null -eq $exitCode) {
        return 1
    }

    return $exitCode
}

function Test-IsBuildLikeDotnetCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    if ($Arguments.Count -eq 0) {
        return $false
    }

    return $Arguments[0] -in @("build", "restore", "test", "pack", "publish")
}

function New-DotnetDiagnosticPaths {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $safeName = ($Description -replace '[^A-Za-z0-9._-]+', '-').Trim('-')
    if ([string]::IsNullOrWhiteSpace($safeName)) {
        $safeName = "dotnet-command"
    }

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $directory = Join-Path $PSScriptRoot "out\diagnostics\$timestamp-$safeName"
    New-Item -ItemType Directory -Force -Path $directory | Out-Null

    return [pscustomobject]@{
        Directory = $directory
        TextLogPath = Join-Path $directory "msbuild.diag.log"
    }
}

function Get-DiagnosticDotnetArguments {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $diagnosticArguments = [System.Collections.Generic.List[string]]::new()
    $foundVerbosity = $false

    for ($index = 0; $index -lt $Arguments.Count; $index++) {
        $argument = $Arguments[$index]
        if ($argument -eq '-v' -or $argument -eq '--verbosity') {
            $diagnosticArguments.Add($argument)
            if ($index + 1 -lt $Arguments.Count) {
                $diagnosticArguments.Add('diag')
                $index++
            }
            $foundVerbosity = $true
            continue
        }

        if ($argument -like '-v:*' -or $argument -like '--verbosity:*') {
            $separatorIndex = $argument.IndexOf(':')
            $prefix = $argument.Substring(0, $separatorIndex + 1)
            $diagnosticArguments.Add("${prefix}diag")
            $foundVerbosity = $true
            continue
        }

        $diagnosticArguments.Add($argument)
    }

    if (-not $foundVerbosity) {
        $diagnosticArguments.Add('-v')
        $diagnosticArguments.Add('diag')
    }

    return @($diagnosticArguments)
}

function Assert-PathExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    $resolvedPath = Resolve-DisplayPath -Path $Path
    if (-not (Test-Path $resolvedPath)) {
        throw $FailureMessage
    }

    Write-Log -Message "Verified path exists: $resolvedPath" -Level "TRACE"
}

function Ensure-ParentDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $resolvedPath = Resolve-DisplayPath -Path $Path
    $parentDirectory = Split-Path -Parent $resolvedPath
    if (-not [string]::IsNullOrWhiteSpace($parentDirectory)) {
        New-Item -ItemType Directory -Force -Path $parentDirectory | Out-Null
        Write-Log -Message "Ensured parent directory exists: $parentDirectory" -Level "TRACE"
    }
}

function Get-TraceImplementationFileMap {
    return @{
        'PlacesGathererRunner' = 'PlacesGatherer.Console/Program.cs'
        'PlacesGatherer.Console.Services.GooglePlacesClient' = 'PlacesGatherer.Console/Services/GooglePlacesClient.cs'
        'PlacesGatherer.Console.Services.PlacesSearchExpander' = 'PlacesGatherer.Console/Services/PlacesSearchExpander.cs'
        'PlacesGatherer.Console.Services.PlaceNameNormalizer' = 'PlacesGatherer.Console/Services/PlaceNameNormalizer.cs'
        'PlacesGatherer.Console.Secrets.SecretProviderFactory' = 'PlacesGatherer.Console/Secrets/SecretProviderFactory.cs'
        'PlacesGatherer.Console.Secrets.LocalConfigurationSecretProvider' = 'PlacesGatherer.Console/Secrets/LocalConfigurationSecretProvider.cs'
        'MasterListBuilderRunner' = 'MasterListBuilder.Console/Program.cs'
        'LocationAssemblerRunner' = 'LocationAssembler.Console/Program.cs'
        'KmlConsoleRunner' = 'KmlGenerator.Console/Program.cs'
        'KmlTilerRunner' = 'KmlTiler.Console/Program.cs'
        'ArcGeometryExtractorApp' = 'ArcGeometryExtractor.Console/Program.cs'
        'KmlGenerator.Core.Services.KmlGenerationService' = 'KmlGenerator.Core/Services/KmlGenerationService.cs'
    }
}

function Get-ProxiedRuntimeFiles {
    return @(
        'ArcGeometryExtractor.Console/Program.cs'
        'KmlGenerator.Console/Program.cs'
        'KmlGenerator.Core/Services/KmlGenerationService.cs'
        'KmlTiler.Console/Program.cs'
        'LocationAssembler.Console/Program.cs'
        'MasterListBuilder.Console/Program.cs'
        'PlacesGatherer.Console/Program.cs'
        'PlacesGatherer.Console/Secrets/LocalConfigurationSecretProvider.cs'
        'PlacesGatherer.Console/Secrets/SecretProviderFactory.cs'
        'PlacesGatherer.Console/Services/GooglePlacesClient.cs'
        'PlacesGatherer.Console/Services/PlaceNameNormalizer.cs'
        'PlacesGatherer.Console/Services/PlacesSearchExpander.cs'
    )
}

function Write-CSharpFileClassificationReport {
    param(
        [Parameter(Mandatory = $true)] [string]$RepoRoot,
        [Parameter(Mandatory = $true)] [string]$OutputPath
    )

    $proxiedRuntimeFiles = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($relativePath in (Get-ProxiedRuntimeFiles)) {
        [void]$proxiedRuntimeFiles.Add($relativePath)
    }

    $items = Get-ChildItem -Path $RepoRoot -Recurse -Filter *.cs | Where-Object {
        $_.FullName -notmatch '\\(bin|obj)\\'
    } | ForEach-Object {
        $relativePath = Get-RelativeDisplayPath -BasePath $RepoRoot -TargetPath $_.FullName
        $classification = if ($relativePath -like 'KmlGenerator.Tests/*' -or $relativePath -like 'PlacesGatherer.Console.Tests/*') {
            if ($relativePath -like '*/StubHttpMessageHandler.cs') { 'test-helper' } else { 'test' }
        }
        elseif ($proxiedRuntimeFiles.Contains($relativePath)) {
            'proxied-runtime'
        }
        else {
            'shared-passive'
        }

        [pscustomobject]@{
            path = $relativePath
            classification = $classification
        }
    } | Sort-Object path

    $directory = Split-Path -Parent $OutputPath
    if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    $items | ConvertTo-Json -Depth 4 | Set-Content $OutputPath
    Write-Log -Message "Wrote C# file classification report to $OutputPath" -Level "TRACE"
}

function Initialize-TraceArtifacts {
    param(
        [Parameter(Mandatory = $true)] [string]$RepoRoot,
        [Parameter(Mandatory = $true)] [string]$TraceDirectory
    )

    New-Item -ItemType Directory -Force -Path $TraceDirectory | Out-Null
    $script:TraceEventsPath = Join-Path $TraceDirectory 'proxy-events.jsonl'
    $script:TraceSummaryPath = Join-Path $TraceDirectory 'runtime-hit-summary.json'
    $script:TraceClassificationPath = Join-Path $TraceDirectory 'csharp-file-classification.json'

    Set-Content -Path $script:TraceEventsPath -Value '' -NoNewline

    $env:KMLSUITE_TRACE_EVENTS_PATH = $script:TraceEventsPath
    Write-CSharpFileClassificationReport -RepoRoot $RepoRoot -OutputPath $script:TraceClassificationPath
    Write-Log -Message "Initialized trace artifacts in $TraceDirectory" -Level "TRACE"
}

function Write-TraceSummaryReport {
    param(
        [Parameter(Mandatory = $true)] [string]$EventsPath,
        [Parameter(Mandatory = $true)] [string]$OutputPath
    )

    $implementationMap = Get-TraceImplementationFileMap
    $events = @()
    if (Test-Path $EventsPath) {
        $events = @(Get-Content $EventsPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json })
    }

    $summary = $events |
        Group-Object ImplementationType, MethodName, Outcome |
        ForEach-Object {
            $first = $_.Group | Select-Object -First 1
            [pscustomobject]@{
                implementationType = $first.ImplementationType
                methodName = $first.MethodName
                outcome = $first.Outcome
                hitCount = $_.Count
                sourceFile = if ($implementationMap.ContainsKey($first.ImplementationType)) { $implementationMap[$first.ImplementationType] } else { $null }
            }
        } | Sort-Object implementationType, methodName, outcome

    $payload = [pscustomobject]@{
        generatedAtUtc = ([DateTime]::UtcNow.ToString('o'))
        eventCount = $events.Count
        touchedFiles = @($summary | Where-Object { $_.sourceFile } | Select-Object -ExpandProperty sourceFile -Unique | Sort-Object)
        methods = @($summary)
    }

    $directory = Split-Path -Parent $OutputPath
    if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    $payload | ConvertTo-Json -Depth 6 | Set-Content $OutputPath
    Write-Log -Message "Wrote trace summary report to $OutputPath" -Level "TRACE"
}

function Complete-TraceArtifacts {
    if ($script:TraceEventsPath -and $script:TraceSummaryPath) {
        Write-TraceSummaryReport -EventsPath $script:TraceEventsPath -OutputPath $script:TraceSummaryPath
    }

    Remove-Item Env:KMLSUITE_TRACE_EVENTS_PATH -ErrorAction SilentlyContinue
}
