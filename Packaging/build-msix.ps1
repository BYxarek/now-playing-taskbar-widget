# Constroi o pacote MSIX para a Microsoft Store.
#
# Pre-requisito (uma vez): editar AppxManifest.xml e substituir a identidade
# pelos valores do Partner Center (Product identity, depois de reservar o nome).
#
# Uso:  powershell -ExecutionPolicy Bypass -File Packaging\build-msix.ps1
# Saida: Packaging\out\SpotifyTaskbarWidget.msix (NAO assinado - a Store assina)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$staging = Join-Path $PSScriptRoot "staging"
$out = Join-Path $PSScriptRoot "out"

# 1. Publish self-contained (a Store nao instala o .NET runtime por nos)
Write-Host "== dotnet publish (self-contained) =="
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
& $dotnet publish (Join-Path $root "SpotifyTaskbarWidget.csproj") -c Release `
    -o $staging -p:SelfContained=true -p:PublishSingleFile=false --nologo
if ($LASTEXITCODE -ne 0) { throw "publish falhou" }

# 2. Manifesto + assets
Copy-Item (Join-Path $PSScriptRoot "AppxManifest.xml") $staging -Force
Copy-Item (Join-Path $PSScriptRoot "Assets") (Join-Path $staging "Assets") -Recurse -Force
$manifest = Get-Content (Join-Path $staging "AppxManifest.xml") -Raw
if ($manifest -match "00000000-0000-0000-0000-000000000000") {
    Write-Warning "Identity/Publisher ainda tem o GUID de exemplo - substituir pelos valores do Partner Center antes de submeter."
}

# 3. makeappx (SDK BuildTools via NuGet, sem instalar o Windows SDK completo)
$sdkVersion = "10.0.28000.2957"
$tools = Join-Path $PSScriptRoot "sdk-tools\$sdkVersion"
$makeappx = Join-Path $tools "bin\10.0.28000.0\x64\makeappx.exe"
if (-not (Test-Path $makeappx)) {
    Write-Host "== a obter Microsoft.Windows.SDK.BuildTools (NuGet) =="
    $nupkg = Join-Path $env:TEMP "sdk-buildtools-$sdkVersion.zip"
    try {
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$sdkVersion/microsoft.windows.sdk.buildtools.$sdkVersion.nupkg" -OutFile $nupkg
        Expand-Archive $nupkg $tools -Force
    } finally {
        Remove-Item $nupkg -ErrorAction SilentlyContinue
    }
}

# 4. Empacotar
New-Item -ItemType Directory -Force $out | Out-Null
$msix = Join-Path $out "SpotifyTaskbarWidget.msix"
if (Test-Path $msix) { Remove-Item $msix -Force }
& $makeappx pack /d $staging /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx falhou" }
Write-Host "OK: $msix ($([math]::Round((Get-Item $msix).Length/1MB,1)) MB)"
Write-Host "Submeter no Partner Center: o pacote segue sem assinatura (a Store assina)."
