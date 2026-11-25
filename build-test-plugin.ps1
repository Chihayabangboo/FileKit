<#
.SYNOPSIS
    构建 FileKit 测试插件并生成根目录下的 test.fkpkg。

.DESCRIPTION
    1. 清理旧 test.fkpkg 与临时打包目录
    2. 编译 FileKit.TestPlugin（Release）
    3. 把插件 DLL 复制到临时目录的 libs/
    4. 生成 manifest.json（version/minHostVersion = 1.0.1）
    5. 使用 Compress-Archive 打包为 test.fkpkg
    6. 回读 ZIP 校验包结构

    兼容 PowerShell 5.1+；任何步骤失败都会返回非零退出码。
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$scriptExitCode = 0

$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($root)) {
    $root = (Get-Location).Path
}

$packagePath = Join-Path $root 'test.fkpkg'
$stagingArchive = Join-Path $root 'test.fkpkg.tmp.zip'
$projectPath = Join-Path $root 'FileKit.TestPlugin\FileKit.TestPlugin.csproj'
$stagingRoot = Join-Path $root 'artifacts\build-test-plugin'
$stagingLibs = Join-Path $stagingRoot 'libs'
$pluginOutputDirectory = Join-Path $root "FileKit.TestPlugin\bin\$Configuration\net8.0-windows"
$pluginDllPath = Join-Path $pluginOutputDirectory 'FileKit.TestPlugin.dll'
$manifestPath = Join-Path $stagingRoot 'manifest.json'

function Remove-PathIfExists {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

try {
    Write-Host '[1/6] 清理旧产物...'
    if (Test-Path -LiteralPath $packagePath) {
        Remove-Item -LiteralPath $packagePath -Force
    }
    if (Test-Path -LiteralPath $stagingArchive) {
        Remove-Item -LiteralPath $stagingArchive -Force
    }
    Remove-PathIfExists $stagingRoot
    New-Item -ItemType Directory -Path $stagingLibs -Force | Out-Null

    Write-Host '[2/6] 编译 FileKit.TestPlugin...'
    & dotnet build $projectPath -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "编译 FileKit.TestPlugin 失败（dotnet 退出码 $LASTEXITCODE）。"
    }

    Write-Host '[3/6] 复制插件 DLL 到临时目录...'
    if (-not (Test-Path -LiteralPath $pluginDllPath)) {
        throw "未找到编译产物：$pluginDllPath"
    }
    Copy-Item -LiteralPath $pluginDllPath -Destination (Join-Path $stagingLibs 'FileKit.TestPlugin.dll') -Force

    # 复制其他依赖 DLL，但排除共享契约与宿主/管理器程序集（契约必须由 Host 的 Default ALC 提供）。
    $excludedDllNames = @(
        'FileKit.Contracts.dll',
        'FileKit.Host.dll',
        'FileKit.PluginManager.dll'
    )
    $dependencyDlls = @(Get-ChildItem -LiteralPath $pluginOutputDirectory -Filter '*.dll' -File |
        Where-Object {
            ($excludedDllNames -notcontains $_.Name) -and
            ($_.Name -notmatch '^(System|Microsoft|WindowsBase|PresentationCore|PresentationFramework|netstandard|mscorlib)\.')
        })
    foreach ($dependencyDll in $dependencyDlls) {
        if ($dependencyDll.FullName -ne $pluginDllPath) {
            Copy-Item -LiteralPath $dependencyDll.FullName -Destination (Join-Path $stagingLibs $dependencyDll.Name) -Force
        }
    }

    Write-Host '[4/6] 生成 manifest.json...'
    $manifest = [ordered]@{
        id             = 'filekit.test-plugin'
        name           = '测试插件'
        version        = '1.0.1'
        minHostVersion = '1.0.1'
        entryAssembly  = 'libs/FileKit.TestPlugin.dll'
        entryType      = 'FileKit.TestPlugin.TestPlugin'
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 4
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8NoBom)

    Write-Host '[5/6] 打包 test.fkpkg...'
    # PowerShell 5.1 的 Compress-Archive 只接受 .zip 扩展名，且生成的分隔符可能是反斜杠；
    # 先压到临时 .zip，再把 entry 名规范化为 / 后写入 .fkpkg（ZIP 规范要求 /）。
    $stagingArchive = Join-Path $root 'test.fkpkg.tmp.zip'
    Compress-Archive -Path (Join-Path $stagingRoot '*') -DestinationPath $stagingArchive -Force
    if (-not (Test-Path -LiteralPath $stagingArchive)) {
        throw '未生成临时 ZIP 包。'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $sourceArchive = [System.IO.Compression.ZipFile]::OpenRead($stagingArchive)
    try {
        $destinationStream = [System.IO.File]::Create($packagePath)
        try {
            $destinationArchive = New-Object -TypeName System.IO.Compression.ZipArchive -ArgumentList @(
                $destinationStream,
                [System.IO.Compression.ZipArchiveMode]::Create)
            try {
                foreach ($sourceEntry in $sourceArchive.Entries) {
                    $normalizedName = ($sourceEntry.FullName -replace '\\', '/')
                    $destinationEntry = $destinationArchive.CreateEntry($normalizedName)
                    $destinationEntry.LastWriteTime = $sourceEntry.LastWriteTime
                    $sourceStream = $sourceEntry.Open()
                    $destinationEntryStream = $destinationEntry.Open()
                    try {
                        $sourceStream.CopyTo($destinationEntryStream)
                    }
                    finally {
                        $destinationEntryStream.Dispose()
                        $sourceStream.Dispose()
                    }
                }
            }
            finally {
                $destinationArchive.Dispose()
            }
        }
        finally {
            $destinationStream.Dispose()
        }
    }
    finally {
        $sourceArchive.Dispose()
    }

    Remove-Item -LiteralPath $stagingArchive -Force
    if (-not (Test-Path -LiteralPath $packagePath)) {
        throw '未生成 test.fkpkg。'
    }

    Write-Host '[6/6] 校验包结构...'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entryNames = @($archive.Entries | ForEach-Object { ($_.FullName -replace '\\', '/') })
        if ($entryNames -notcontains 'manifest.json') {
            throw 'test.fkpkg 根目录缺少 manifest.json。'
        }
        if ($entryNames -notcontains 'libs/FileKit.TestPlugin.dll') {
            throw 'test.fkpkg 缺少 libs/FileKit.TestPlugin.dll。'
        }
    }
    finally {
        $archive.Dispose()
    }

    $packageSize = (Get-Item -LiteralPath $packagePath).Length
    Write-Host ''
    Write-Host "打包成功：$packagePath（$packageSize 字节）" -ForegroundColor Green
    Write-Host 'ZIP 内容：'
    $archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $archive.Entries | ForEach-Object { Write-Host ("  - " + $_.FullName) }
    }
    finally {
        $archive.Dispose()
    }
}
catch {
    Write-Host ''
    Write-Host ("打包失败：" + $_.Exception.Message) -ForegroundColor Red
    Write-Error $_
    $scriptExitCode = 1
}
finally {
    Remove-PathIfExists $stagingRoot
    if (Test-Path -LiteralPath $stagingArchive) {
        Remove-Item -LiteralPath $stagingArchive -Force -ErrorAction SilentlyContinue
    }
}

exit $scriptExitCode
