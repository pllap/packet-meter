$ErrorActionPreference = 'Stop'

$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw '.NET Framework C# compiler was not found.' }

$outputDir = Join-Path $PSScriptRoot '.test-output'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$testExe = Join-Path $outputDir 'PacketMeter.Tests.exe'
$source = Join-Path $PSScriptRoot 'src\PacketMeter.cs'
$tests = Join-Path $PSScriptRoot 'tests\PacketMeter.Tests.cs'
& $compiler /nologo /codepage:65001 /target:exe /main:PacketMeterTests "/out:$testExe" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll $source $tests
if ($LASTEXITCODE -ne 0) { throw "Test compilation failed with exit code $LASTEXITCODE." }
& $testExe $outputDir
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
