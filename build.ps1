# WinFresh Studio - One-Click Standalone Build Script
$ErrorActionPreference = "Stop"

Write-Host "Building WinFresh Studio (win-x64, single-file, self-contained)..." -ForegroundColor Cyan

dotnet publish src/WinFresh/WinFresh.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  /p:PublishSingleFile=true `
  -o publish/win-x64

Write-Host "`nBuild succeeded! Executable ready at:" -ForegroundColor Green
Write-Host "  publish\win-x64\WinFresh.exe" -ForegroundColor White
