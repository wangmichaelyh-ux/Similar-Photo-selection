$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /optimize+ /platform:anycpu /win32manifest:app.manifest /win32icon:app.ico /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /out:PhotoSieve.exe PhotoSieve.cs UiControls.cs AssemblyInfo.cs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    Write-Host 'Built PhotoSieve.exe'
} finally { Pop-Location }
