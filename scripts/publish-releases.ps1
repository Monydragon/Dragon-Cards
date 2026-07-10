param(
    [string]$Configuration = "Release",
    [string]$Version,
    [ValidateSet("win-x64", "win-arm64", "linux-x64", "osx-x64", "osx-arm64")]
    [string[]]$RuntimeIdentifiers = @("win-x64")
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    return Split-Path -Parent $PSScriptRoot
}

function Get-VersionDocument {
    param([string]$Path)
    return [xml](Get-Content -LiteralPath $Path -Raw)
}

function Get-VersionValue {
    param([xml]$Document)
    $node = $Document.SelectSingleNode("/Project/PropertyGroup/Version")
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "Directory.Build.props must contain a Version property."
    }

    return $node.InnerText.Trim()
}

function Convert-ToVersionParts {
    param([string]$Value)
    if ($Value -notmatch "^(\d+)\.(\d+)\.(\d+)$") {
        throw "Version '$Value' must use major.minor.patch format."
    }

    return [pscustomobject]@{
        Major = [int]$Matches[1]
        Minor = [int]$Matches[2]
        Patch = [int]$Matches[3]
    }
}

function Compare-VersionParts {
    param([object]$Left, [object]$Right)
    foreach ($property in @("Major", "Minor", "Patch")) {
        if ($Left.$property -lt $Right.$property) { return -1 }
        if ($Left.$property -gt $Right.$property) { return 1 }
    }

    return 0
}

function Format-VersionParts {
    param([object]$Parts)
    return "{0}.{1}.{2}" -f $Parts.Major, $Parts.Minor, $Parts.Patch
}

function Set-XmlProperty {
    param([xml]$Document, [string]$Name, [string]$Value)
    $project = $Document.SelectSingleNode("/Project")
    $group = $Document.SelectSingleNode("/Project/PropertyGroup")
    if ($null -eq $group) {
        $group = $Document.CreateElement("PropertyGroup")
        [void]$project.AppendChild($group)
    }

    $node = $Document.SelectSingleNode("/Project/PropertyGroup/$Name")
    if ($null -eq $node) {
        $node = $Document.CreateElement($Name)
        [void]$group.AppendChild($node)
    }

    $node.InnerText = $Value
}

function Save-VersionDocument {
    param([xml]$Document, [string]$Path)
    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Indent = $true
    $settings.IndentChars = "  "
    $settings.OmitXmlDeclaration = $true
    $settings.NewLineChars = "`r`n"
    $settings.NewLineHandling = [System.Xml.NewLineHandling]::Replace
    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try {
        $Document.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

function Update-VersionMetadata {
    param([string]$PropsPath, [string]$RequestedVersion)
    $document = Get-VersionDocument -Path $PropsPath
    $currentVersion = Get-VersionValue -Document $document
    $currentParts = Convert-ToVersionParts -Value $currentVersion

    if ([string]::IsNullOrWhiteSpace($RequestedVersion)) {
        $currentParts.Patch += 1
        $targetVersion = Format-VersionParts -Parts $currentParts
    }
    else {
        $targetParts = Convert-ToVersionParts -Value $RequestedVersion
        if ((Compare-VersionParts -Left $targetParts -Right $currentParts) -lt 0) {
            throw "Requested version '$RequestedVersion' is older than the current version '$currentVersion'."
        }

        $targetVersion = $RequestedVersion
    }

    Set-XmlProperty -Document $document -Name "Version" -Value $targetVersion
    Set-XmlProperty -Document $document -Name "AssemblyVersion" -Value "$targetVersion.0"
    Set-XmlProperty -Document $document -Name "FileVersion" -Value "$targetVersion.0"
    Set-XmlProperty -Document $document -Name "InformationalVersion" -Value $targetVersion
    Save-VersionDocument -Document $document -Path $PropsPath
    return [pscustomobject]@{ Previous = $currentVersion; Version = $targetVersion }
}

function Get-PlatformFolder {
    param([string]$RuntimeIdentifier)
    if ($RuntimeIdentifier.StartsWith("win-", [StringComparison]::OrdinalIgnoreCase)) { return "Windows" }
    if ($RuntimeIdentifier.StartsWith("linux-", [StringComparison]::OrdinalIgnoreCase)) { return "Linux" }
    if ($RuntimeIdentifier.StartsWith("osx-", [StringComparison]::OrdinalIgnoreCase)) { return "macOS" }
    throw "Unsupported runtime identifier '$RuntimeIdentifier'."
}

function Publish-DesktopRuntime {
    param(
        [string]$Project,
        [string]$RuntimeIdentifier,
        [string]$OutputPath,
        [string]$Configuration
    )

    if (Test-Path -LiteralPath $OutputPath) {
        Remove-Item -LiteralPath $OutputPath -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null

    & dotnet publish $Project `
        --configuration $Configuration `
        --runtime $RuntimeIdentifier `
        --self-contained true `
        --output $OutputPath `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false `
        -p:PublishAot=false
    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed for runtime '$RuntimeIdentifier'."
    }
}

function Copy-DesktopNativeDependencies {
    param(
        [string]$RepoRoot,
        [string]$RuntimeIdentifier,
        [string]$Configuration,
        [string]$OutputPath
    )

    $buildOutput = Join-Path $RepoRoot "src\DragonCards.Desktop\bin\AnyCPU\$Configuration\net10.0\$RuntimeIdentifier"
    if (-not (Test-Path -LiteralPath $buildOutput)) {
        throw "Unable to locate the native KNI runtime output at '$buildOutput'."
    }

    $nativeFiles = Get-ChildItem -LiteralPath $buildOutput -File |
        Where-Object { $_.Name -match '^(SDL2\.dll|soft_oal\.dll|libSDL2.*|libopenal.*)$' }
    if ($nativeFiles.Count -eq 0) {
        throw "No native SDL2/OpenAL files were produced for '$RuntimeIdentifier'."
    }

    foreach ($nativeFile in $nativeFiles) {
        Copy-Item -LiteralPath $nativeFile.FullName -Destination (Join-Path $OutputPath $nativeFile.Name) -Force
    }

    return ($nativeFiles.Name -join ', ')
}

function Compress-Release {
    param([string]$SourcePath, [string]$ZipPath)
    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }

    Compress-Archive -LiteralPath $SourcePath -DestinationPath $ZipPath -Force
}

$repoRoot = Get-RepoRoot
Set-Location $repoRoot
$versionInfo = Update-VersionMetadata -PropsPath (Join-Path $repoRoot "Directory.Build.props") -RequestedVersion $Version
$releaseRoot = Join-Path $repoRoot "artifacts\releases\v$($versionInfo.Version)"
$project = Join-Path $repoRoot "src\DragonCards.Desktop\DragonCards.Desktop.csproj"

if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null

$published = @()
foreach ($runtimeIdentifier in $RuntimeIdentifiers) {
    $platform = Get-PlatformFolder -RuntimeIdentifier $runtimeIdentifier
    $outputPath = Join-Path $releaseRoot "$platform\$runtimeIdentifier"
    Publish-DesktopRuntime -Project $project -RuntimeIdentifier $runtimeIdentifier -OutputPath $outputPath -Configuration $Configuration
    $nativeDependencies = Copy-DesktopNativeDependencies -RepoRoot $repoRoot -RuntimeIdentifier $runtimeIdentifier -Configuration $Configuration -OutputPath $outputPath
    $zipPath = Join-Path $releaseRoot "DragonCards-$platform-$runtimeIdentifier-v$($versionInfo.Version).zip"
    Compress-Release -SourcePath $outputPath -ZipPath $zipPath
    $published += "$platform/$runtimeIdentifier (native: $nativeDependencies)"
}

$manifest = @(
    "Dragon Cards $($versionInfo.Version)",
    "Published UTC: $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss'))",
    "Configuration: $Configuration",
    "Included runtimes: $($published -join ', ')",
    "Packaging: self-contained single-file host with untrimmed managed dependencies and copied game content.",
    "Run the executable from its published runtime folder, or distribute that folder's matching zip."
)
Set-Content -LiteralPath (Join-Path $releaseRoot "RELEASES.txt") -Value $manifest

Write-Host "Dragon Cards release version: $($versionInfo.Previous) -> $($versionInfo.Version)" -ForegroundColor Green
Write-Host "Release artifacts created in $releaseRoot" -ForegroundColor Green
