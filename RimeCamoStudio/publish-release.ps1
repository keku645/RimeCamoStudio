# publish-release.ps1 - Camo Studio as ONE self-contained executable, in bin\publish\RimeCamoStudio.exe.
#
# The user needs nothing else: no .NET installed, no build of Rime, no Windows SDK. Inside the executable:
#   - the .NET runtime (a self-contained single file),
#   - rime\  : RimeREPL published self-contained, with every Frostbite 2 support library it loads at mount time
#              (the list build-release.ps1 builds) and the EBX fidelity map,
#   - texconv.exe (DirectXTex) and d3dcompiler_47.dll (the shader compiler, see Emit\ShaderCompiler.cs),
#   - Data\ : the catalogues of names and hashes, the hand-placed emblem slots, and the starting documents of a few subjects kept as
#             changes to the user's own translations (with recipes for the pictures made of the game's textures).
# Nothing of the game is in it: each user builds his own cache from his own install, inside the studio.
#
# Usage:  ./publish-release.ps1 [-Rime <folder of a Rime checkout>] [-Texconv <texconv.exe>]
#   -Rime     the Rime source tree RimeREPL is built from; by default the folder above this one (the studio inside a Rime checkout)
#   -Texconv  texconv.exe from DirectXTex; by default <Rime>\bin\Release\texconv.exe

param(
    [string]$Rime = (Join-Path $PSScriptRoot ".."),
    [string]$Texconv = ""
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$root = (Resolve-Path $Rime).Path
if (-not (Test-Path (Join-Path $root "Utils\RimeREPL\RimeREPL.csproj"))) {
    throw "No Rime source tree at '$root' (no Utils\RimeREPL\RimeREPL.csproj): pass -Rime <folder of a Rime checkout> (see README.md)."
}
if ($Texconv -eq "") { $Texconv = Join-Path $root "bin\Release\texconv.exe" }
if (-not (Test-Path $Texconv)) {
    throw "texconv.exe not found at '$Texconv': pass -Texconv <path> (DirectXTex releases, see README.md)."
}
# the folder holding RimeCamoStudio and the RimeShaderEditor source it compiles (a Rime checkout, or this repository)
$studioRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$tools = Join-Path $PSScriptRoot "obj\release-tools"
$rime = Join-Path $tools "rime"
$out = Join-Path $PSScriptRoot "bin\publish"

foreach ($dir in @($tools, $out, (Join-Path $PSScriptRoot "obj\release-build"))) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}

# The Frostbite 2 support libraries RimeREPL loads at mount time (not referenced by it: each must be built, see build-release.ps1)...
$support = @(
    "RimeLib.Content.Frostbite2_0"
    "RimeLib.Serialization.Frostbite2_0"
    "RimeLib.Texture.Frostbite2_0"
    "RimeLib.Shader.Frostbite2_0"
    "RimeLib.Havok.Frostbite2_0"
    "RimeLib.Mesh.Frostbite2_0"
    "RimeLib.Animation.Frostbite2_0"
    "RimeLib.Terrain.Frostbite2_0"
    "RimeLib.Toolkit.Frostbite2_0"
)
# No debug information and no source paths in anything the release carries: a build otherwise writes the build machine's folders into
# every assembly (the PDB path in its debug directory, [CallerFilePath] constants) — PathMap rewrites them to /_/.
$clean = @("-p:DebugType=none", "-p:DebugSymbols=false", "-p:PathMap=$root=/_/")

foreach ($p in $support) {
    Write-Host "==> $p" -ForegroundColor Cyan
    dotnet build (Join-Path $root "$p\$p.csproj") -c Release -o $rime --nologo -p:SolutionDir="$root/" @clean
    if ($LASTEXITCODE -ne 0) { throw "build failed: $p" }
}

# ...then RimeREPL itself, self-contained, over them — built in a folder of its own (OutputPath): the repository's bin\Release, which a
# running RimeREPL may hold, is never written.
Write-Host "==> RimeREPL (self-contained)" -ForegroundColor Cyan
$build = Join-Path $PSScriptRoot "obj\release-build"
dotnet publish (Join-Path $root "Utils\RimeREPL\RimeREPL.csproj") -c Release -r win-x64 --self-contained true -o $rime --nologo `
    -p:SolutionDir="$root/" -p:OutputPath="$build/" @clean
if ($LASTEXITCODE -ne 0) { throw "publish failed: RimeREPL" }

Copy-Item (Join-Path $root "fidelity_fb2.json") $rime
Copy-Item $Texconv (Join-Path $tools "texconv.exe")

# No debug symbols in a release: they are left beside the single file (never bundled) and carry the build machine's source paths.
Get-ChildItem $tools -Recurse -Include *.pdb | Remove-Item -Force

# The studio, one file: its content (Data, the tools above, the shader compiler) unpacked at start-up beside it.
Write-Host "==> Camo Studio (single self-contained file)" -ForegroundColor Cyan
$studioClean = @("-p:DebugType=none", "-p:DebugSymbols=false", "-p:PathMap=$studioRoot=/_/")
dotnet publish (Join-Path $PSScriptRoot "RimeCamoStudio.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:SolutionDir="$studioRoot/" -o $out --nologo @studioClean
if ($LASTEXITCODE -ne 0) { throw "publish failed: RimeCamoStudio" }

Write-Host ""
Write-Host "Release ready: $out\RimeCamoStudio.exe" -ForegroundColor Green
Get-ChildItem $out | Format-Table Name, Length
