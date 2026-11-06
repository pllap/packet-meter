$ErrorActionPreference = 'Stop'

# The .NET Framework compiler ships with Windows; a separate SDK is not required.
$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw '.NET Framework C# compiler was not found.' }

$outputDir = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$source = Join-Path $PSScriptRoot 'src\PacketMeter.cs'
$output = Join-Path $outputDir 'PacketMeter.exe'

& $compiler /nologo /codepage:65001 /target:winexe /optimize+ "/out:$output" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll $source
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE." }
Write-Host "Built: $output"
