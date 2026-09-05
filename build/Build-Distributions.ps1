[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$InnoCompilerPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $repositoryRoot "src\RxV4A.Desktop\RxV4A.Desktop.csproj"
$propertiesPath = Join-Path $repositoryRoot "Directory.Build.props"
$distributionRoot = Join-Path $repositoryRoot "artifacts\distribution"

[xml]$buildProperties = Get-Content -LiteralPath $propertiesPath
$versionNode = $buildProperties.SelectSingleNode("//VersionPrefix")
if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
    throw "Directory.Build.propsからVersionPrefixを取得できませんでした。"
}

$version = $versionNode.InnerText.Trim()
$portableName = "NAVA-$version-win-x64-portable"
$portableDirectory = Join-Path $distributionRoot $portableName
$publishDirectory = Join-Path $distributionRoot "publish"
$portableArchive = Join-Path $distributionRoot "$portableName.zip"

$distributionFullPath = [System.IO.Path]::GetFullPath($distributionRoot)
$artifactsFullPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
if (-not $distributionFullPath.StartsWith(
        $artifactsFullPath + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "配布物の出力先がartifacts配下ではありません。"
}

if (Test-Path -LiteralPath $distributionFullPath) {
    Remove-Item -LiteralPath $distributionFullPath -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $portableDirectory "doc") -Force | Out-Null

dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --output $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "NAVAの発行に失敗しました。"
}

Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $portableDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $repositoryRoot "README.md") -Destination $portableDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot "CHANGELOG.md") -Destination $portableDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot "LICENSE") -Destination $portableDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot "doc\USER_GUIDE.md") -Destination (Join-Path $portableDirectory "doc")
Copy-Item -LiteralPath (Join-Path $repositoryRoot "doc\API_GUIDE.md") -Destination (Join-Path $portableDirectory "doc")
Copy-Item -LiteralPath (Join-Path $repositoryRoot "doc\NAVA03.png") -Destination (Join-Path $portableDirectory "doc")
Copy-Item -LiteralPath (Join-Path $repositoryRoot "doc\NAVA04.png") -Destination (Join-Path $portableDirectory "doc")
Copy-Item -LiteralPath (Join-Path $repositoryRoot "doc\NAVA05.png") -Destination (Join-Path $portableDirectory "doc")

Compress-Archive -LiteralPath $portableDirectory -DestinationPath $portableArchive -CompressionLevel Optimal

if ([string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
    $knownCompilerPaths = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )
    $InnoCompilerPath = $knownCompilerPaths |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($InnoCompilerPath) -or
    -not (Test-Path -LiteralPath $InnoCompilerPath)) {
    throw "Inno Setup 6が見つかりません。-InnoCompilerPathでISCC.exeを指定してください。"
}

$installerScript = Join-Path $repositoryRoot "installer\NAVA.iss"
& $InnoCompilerPath `
    "/DMyAppVersion=$version" `
    "/DSourceDir=$portableDirectory" `
    "/DOutputDir=$distributionRoot" `
    $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "NAVAインストーラーの作成に失敗しました。"
}

Remove-Item -LiteralPath $publishDirectory -Recurse -Force

$installerPath = Join-Path $distributionRoot "NAVA-$version-win-x64-setup.exe"
$checksumPath = Join-Path $distributionRoot "SHA256SUMS.txt"
$checksums = Get-FileHash -Algorithm SHA256 -LiteralPath $portableArchive, $installerPath
$checksumLines = $checksums | ForEach-Object {
    "$($_.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($_.Path))"
}
Set-Content -LiteralPath $checksumPath -Value $checksumLines -Encoding utf8NoBOM

Get-Item -LiteralPath $portableArchive, $installerPath, $checksumPath |
    Select-Object Name, Length, LastWriteTime
