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

    # Own the dotnet child process directly so interrupted script runs do not leave stale dotnet workers
    # holding obj/bin files open. Redirect to temp files, poll them, and kill the process in cleanup if needed.
    $formattedArguments = $Arguments | ForEach-Object { Format-CommandArgument -Value $_ }
    $stdoutPath = Join-Path ([System.IO.Path]::GetTempPath()) ("kmlsuite-dotnet-stdout-" + [guid]::NewGuid().ToString() + ".log")
    $stderrPath = Join-Path ([System.IO.Path]::GetTempPath()) ("kmlsuite-dotnet-stderr-" + [guid]::NewGuid().ToString() + ".log")
    $stdoutOffset = 0L
    $stderrOffset = 0L
    $stdoutRemainder = ""
    $stderrRemainder = ""
    $process = $null
    $writer = $null
    if ($LogPath) {
        Ensure-ParentDirectory -Path $LogPath
        $writer = [System.IO.StreamWriter]::new((Resolve-DisplayPath -Path $LogPath), $false, [System.Text.Encoding]::UTF8)
        $writer.AutoFlush = $true
    }

    try {
        $process = Start-Process -FilePath "dotnet" `
            -ArgumentList ($formattedArguments -join ' ') `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -PassThru `
            -WindowStyle Hidden
        $null = $process.Handle

        while ($true) {
            $stdoutOffset, $stdoutRemainder = Write-AppendedLogContent -Path $stdoutPath -Offset $stdoutOffset -Remainder $stdoutRemainder -Writer $writer
            $stderrOffset, $stderrRemainder = Write-AppendedLogContent -Path $stderrPath -Offset $stderrOffset -Remainder $stderrRemainder -Writer $writer

            if ($process.HasExited) {
                break
            }

            Start-Sleep -Milliseconds 250
        }

        $stdoutOffset, $stdoutRemainder = Write-AppendedLogContent -Path $stdoutPath -Offset $stdoutOffset -Remainder $stdoutRemainder -Writer $writer -FlushRemainder
        $stderrOffset, $stderrRemainder = Write-AppendedLogContent -Path $stderrPath -Offset $stderrOffset -Remainder $stderrRemainder -Writer $writer -FlushRemainder
        $process.WaitForExit()
        $exitCode = $process.ExitCode
    }
    finally {
        if ($writer) {
            $writer.Dispose()
        }

        if ($process) {
            try {
                if (-not $process.HasExited) {
                    $process.Kill($true)
                    $process.WaitForExit()
                }
            }
            catch {
            }

            $process.Dispose()
        }

        Remove-Item -Path $stdoutPath -Force -ErrorAction SilentlyContinue
        Remove-Item -Path $stderrPath -Force -ErrorAction SilentlyContinue
    }

    if ($null -eq $exitCode) {
        return 1
    }

    return $exitCode
}

function Write-AppendedLogContent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [long]$Offset,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Remainder,

        [System.IO.StreamWriter]$Writer,

        [switch]$FlushRemainder
    )

    if (-not (Test-Path $Path)) {
        return $Offset, $Remainder
    }

    $content = ""
    $stream = $null
    $reader = $null
    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        if ($Offset -gt $stream.Length) {
            $Offset = 0L
            $Remainder = ""
        }

        $stream.Seek($Offset, [System.IO.SeekOrigin]::Begin) | Out-Null
        $reader = [System.IO.StreamReader]::new($stream)
        $content = $reader.ReadToEnd()
        $Offset = $stream.Position
    }
    finally {
        if ($reader) {
            $reader.Dispose()
        }
        elseif ($stream) {
            $stream.Dispose()
        }
    }

    if ([string]::IsNullOrEmpty($content) -and -not $FlushRemainder) {
        return $Offset, $Remainder
    }

    $combined = $Remainder + $content
    $lines = $combined -split "(`r`n|`n|`r)"
    $newRemainder = ""
    if (-not $FlushRemainder -and $combined.Length -gt 0 -and $combined -notmatch "(`r`n|`n|`r)$") {
        $newRemainder = $lines[-1]
        if ($lines.Length -gt 1) {
            $lines = $lines[0..($lines.Length - 2)]
        }
        else {
            $lines = @()
        }
    }

    foreach ($line in $lines) {
        if ($line -is [string] -and $line.Length -gt 0) {
            Write-Host $line
            if ($Writer) {
                $Writer.WriteLine($line)
            }
        }
    }

    if ($FlushRemainder -and -not [string]::IsNullOrEmpty($newRemainder)) {
        Write-Host $newRemainder
        if ($Writer) {
            $Writer.WriteLine($newRemainder)
        }
        $newRemainder = ""
    }

    return $Offset, $newRemainder
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
