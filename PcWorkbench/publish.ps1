$ErrorActionPreference = 'Stop'
dotnet publish "$PSScriptRoot\PcWorkbench.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "$PSScriptRoot\dist"
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
