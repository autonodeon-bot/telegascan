# Portable single-file build (self-contained win-x64, no .NET install on target PC)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
dotnet publish "$here\TelegaScan.csproj" -c Release -r win-x64 -o "$here\publish" -p:DebugType=none -p:DebugSymbols=false -p:PublishDebugSymbols=false --nologo
Write-Host "Output: $here\publish\TelegaScan.exe"
