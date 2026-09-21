$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Requires the .NET Framework 4 x64 C# compiler.' }
$source = Join-Path $PSScriptRoot 'RakuenLauncher.cs'
$icon = Join-Path $PSScriptRoot 'rakuen.ico'
$outputFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'rakuen_compat.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /r:System.Windows.Forms.dll "/win32icon:$icon" "/out:$outputFile" $source
if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $LASTEXITCODE" }
Get-FileHash -LiteralPath $outputFile -Algorithm SHA256 | Format-List
Write-Output 'Build complete. The EXE next to README.md is the only release executable.'
