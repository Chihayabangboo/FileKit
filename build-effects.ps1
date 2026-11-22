<#
.SYNOPSIS
    构建 FileKit dev 特效样例并打包为 artifacts\manual-test\*.fkeffect。

.DESCRIPTION
    1. 构建 FileKit.TestEffects（net8.0-windows，仅引用 Contracts）
    2. 为每个样例生成 staging（根 manifest.json + libs\FileKit.TestEffects.dll）
    3. 用 Compress-Archive 生成临时 .zip，再把 entry 名规范化为 / 后写入 .fkeffect
    4. 回读校验：根 manifest 唯一、其余只在 libs/、无 Zip Slip/重复 entry/反斜杠，
       且 manifest.effectId/entryType/target/entryAssembly 与预期一致
    5. 输出包清单（文件名 / effectId / entryType / target / 字节数）

    兼容 PowerShell 5.1+；任何步骤失败都会返回非零退出码。
    本脚本只会覆盖自己的 6 个 .fkeffect，不会删除 artifacts\manual-test 下的其它文件。
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

$projectPath = Join-Path $root 'FileKit.TestEffects\FileKit.TestEffects.csproj'
$outputDirectory = Join-Path $root "FileKit.TestEffects\bin\$Configuration\net8.0-windows"
$effectAssemblyName = 'FileKit.TestEffects.dll'
$effectAssemblyPath = Join-Path $outputDirectory $effectAssemblyName
$packageRoot = Join-Path $root 'artifacts\manual-test'
$stagingRoot = Join-Path $root 'artifacts\build-effects'
$tempZipPath = Join-Path $stagingRoot 'package.tmp.zip'

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# 包定义：IncludeManifest=$false 仅用于“非法包”验收（缺 manifest）。
$packages = @(
    [pscustomobject]@{
        FileName        = 'test.fkeffect'
        EffectId        = 'filekit.dev.custom-overlay'
        Name            = '自定义 Overlay 特效'
        EntryType       = 'FileKit.TestEffects.CustomOverlayEffect'
        Target          = 'Window'
        IncludeManifest = $true
    },
    [pscustomobject]@{
        FileName        = 'effect-a.fkeffect'
        EffectId        = 'filekit.dev.material-a'
        Name            = '材质特效 A'
        EntryType       = 'FileKit.TestEffects.MaterialEffectA'
        Target          = 'Window'
        IncludeManifest = $true
    },
    [pscustomobject]@{
        FileName        = 'effect-b.fkeffect'
        EffectId        = 'filekit.dev.material-b'
        Name            = '材质特效 B'
        EntryType       = 'FileKit.TestEffects.MaterialEffectB'
        Target          = 'Window'
        IncludeManifest = $true
    },
    [pscustomobject]@{
        FileName        = 'bad-throwing.fkeffect'
        EffectId        = 'filekit.dev.faulty'
        Name            = '故障特效（Attach 抛异常）'
        EntryType       = 'FileKit.TestEffects.FaultyEffect'
        Target          = 'Window'
        IncludeManifest = $true
    },
    [pscustomobject]@{
        # 故意小写 target：EffectTarget 映射大小写敏感，EffectManager 必须拒绝。
        FileName        = 'bad-invalid-target.fkeffect'
        EffectId        = 'filekit.dev.invalid-target'
        Name            = '非法 target 特效'
        EntryType       = 'FileKit.TestEffects.CustomOverlayEffect'
        Target          = 'window'
        IncludeManifest = $true
    },
    [pscustomobject]@{
        # 故意缺 manifest：EffectManager 必须在安装校验阶段拒绝。
        FileName        = 'bad-missing-manifest.fkeffect'
        EffectId        = 'filekit.dev.missing-manifest'
        Name            = '缺 manifest 包'
        EntryType       = 'FileKit.TestEffects.CustomOverlayEffect'
        Target          = 'Window'
        IncludeManifest = $false
    }
)

function Remove-PathIfExists {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Write-EffectManifest {
    param(
        [string]$Path,
        [string]$EffectId,
        [string]$Name,
        [string]$EntryType,
        [string]$Target
    )

    $manifest = [ordered]@{
        effectId       = $EffectId
        name           = $Name
        version        = '1.0.0'
        minHostVersion = '1.0.0'
        entryAssembly  = 'libs/FileKit.TestEffects.dll'
        entryType      = $EntryType
        target         = $Target
    }

    $json = $manifest | ConvertTo-Json -Depth 4
    [System.IO.File]::WriteAllText($Path, $json, $utf8NoBom)
}

function Compress-EffectPackage {
    param(
        [string]$SourceDirectory,
        [string]$PackagePath
    )

    if (Test-Path -LiteralPath $tempZipPath) {
        Remove-Item -LiteralPath $tempZipPath -Force
    }

    # PowerShell 5.1 的 Compress-Archive 只接受 .zip 扩展名，且 entry 分隔符可能是反斜杠；
    # 先压到临时 .zip，再把 entry 名规范化为 / 后写入 .fkeffect（ZIP 规范要求 /）。
    Compress-Archive -Path (Join-Path $SourceDirectory '*') -DestinationPath $tempZipPath -Force
    if (-not (Test-Path -LiteralPath $tempZipPath)) {
        throw "未生成临时 ZIP：$tempZipPath"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $sourceArchive = [System.IO.Compression.ZipFile]::OpenRead($tempZipPath)
    try {
        $destinationStream = [System.IO.File]::Create($PackagePath)
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
        if (Test-Path -LiteralPath $tempZipPath) {
            Remove-Item -LiteralPath $tempZipPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-ZipEntryNames {
    param([string]$PackagePath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $names = @()
        foreach ($entry in $archive.Entries) {
            $names += ($entry.FullName -replace '\\', '/')
        }

        return $names
    }
    finally {
        $archive.Dispose()
    }
}

function Read-ZipManifest {
    param([string]$PackagePath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $archive.GetEntry('manifest.json')
        if ($null -eq $entry) {
            throw "包内缺少 manifest.json：$PackagePath"
        }

        $stream = $entry.Open()
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
        try {
            $text = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
            $stream.Dispose()
        }

        return ($text | ConvertFrom-Json)
    }
    finally {
        $archive.Dispose()
    }
}

function Test-EffectPackage {
    param(
        [string]$PackagePath,
        [object]$Spec
    )

    $entryNames = @(Get-ZipEntryNames -PackagePath $PackagePath)
    $expectedDllEntry = 'libs/FileKit.TestEffects.dll'

    foreach ($name in $entryNames) {
        if ([string]::IsNullOrWhiteSpace($name)) {
            throw "包内存在空 entry：$PackagePath"
        }
        if ($name.StartsWith('/')) {
            throw "包内存在绝对路径 entry：$name"
        }
        if ($name.Contains(':')) {
            throw "包内存在盘符/冒号 entry：$name"
        }
        if ($name.Contains('\')) {
            throw "包内 entry 未规范化为 /：$name"
        }

        $segments = $name.Split('/')
        foreach ($segment in $segments) {
            if ($segment -eq '.' -or $segment -eq '..') {
                throw "包内存在 . / .. 路径段（疑似 Zip Slip）：$name"
            }
        }
    }

    $duplicates = @($entryNames | Group-Object | Where-Object { $_.Count -gt 1 })
    if ($duplicates.Count -gt 0) {
        throw "包内存在重复 entry：$($duplicates[0].Name)"
    }

    if ($entryNames -notcontains $expectedDllEntry) {
        throw "包内缺少入口程序集 entry：$expectedDllEntry"
    }

    if ($Spec.IncludeManifest) {
        if ($entryNames -notcontains 'manifest.json') {
            throw "根目录缺少 manifest.json：$PackagePath"
        }
        if ($entryNames.Count -ne 2) {
            throw "包内文件数量异常（应仅 manifest.json + libs DLL）：$($entryNames -join ', ')"
        }

        $manifest = Read-ZipManifest -PackagePath $PackagePath
        if ($manifest.effectId -cne $Spec.EffectId) {
            throw "manifest.effectId 不符：$($manifest.effectId) != $($Spec.EffectId)"
        }
        if ($manifest.entryType -cne $Spec.EntryType) {
            throw "manifest.entryType 不符：$($manifest.entryType) != $($Spec.EntryType)"
        }
        if ($manifest.target -cne $Spec.Target) {
            throw "manifest.target 不符：$($manifest.target) != $($Spec.Target)"
        }
        if ($manifest.entryAssembly -cne 'libs/FileKit.TestEffects.dll') {
            throw "manifest.entryAssembly 不符：$($manifest.entryAssembly)"
        }
    }
    else {
        if ($entryNames -contains 'manifest.json') {
            throw "非法包不应包含 manifest.json：$PackagePath"
        }
        if ($entryNames.Count -ne 1) {
            throw "非法包只应含 libs DLL：$($entryNames -join ', ')"
        }
    }
}

try {
    Write-Host '[1/6] 准备输出目录...'
    New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
    Remove-PathIfExists $stagingRoot
    New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

    Write-Host '[2/6] 编译 FileKit.TestEffects...'
    & dotnet build $projectPath -c $Configuration --nologo -m:1 -p:BuildInParallel=false
    if ($LASTEXITCODE -ne 0) {
        throw "编译 FileKit.TestEffects 失败（dotnet 退出码 $LASTEXITCODE）。"
    }
    if (-not (Test-Path -LiteralPath $effectAssemblyPath)) {
        throw "未找到编译产物：$effectAssemblyPath"
    }

    Write-Host '[3/6] 生成包内容并压缩...'
    $excludedDllNames = @(
        'FileKit.Contracts.dll',
        'FileKit.Host.dll',
        'FileKit.PluginManager.dll',
        'FileKit.EffectManager.dll',
        'FileKit.TestPlugin.dll',
        'FileKit.TestEffect.dll'
    )

    foreach ($spec in $packages) {
        $stageDirectory = Join-Path $stagingRoot ([System.IO.Path]::GetFileNameWithoutExtension($spec.FileName))
        Remove-PathIfExists $stageDirectory
        $stageLibs = Join-Path $stageDirectory 'libs'
        New-Item -ItemType Directory -Force -Path $stageLibs | Out-Null

        Copy-Item -LiteralPath $effectAssemblyPath -Destination (Join-Path $stageLibs $effectAssemblyName) -Force

        # 依赖 DLL 兜底复制，排除共享契约/宿主/框架 DLL；当前样例输出只有自身 DLL。
        $dependencyDlls = @(Get-ChildItem -LiteralPath $outputDirectory -Filter '*.dll' -File |
            Where-Object {
                ($excludedDllNames -notcontains $_.Name) -and
                ($_.Name -ne $effectAssemblyName) -and
                ($_.Name -notmatch '^(System|Microsoft|WindowsBase|PresentationCore|PresentationFramework|netstandard|mscorlib)\.')
            })
        foreach ($dependencyDll in $dependencyDlls) {
            Copy-Item -LiteralPath $dependencyDll.FullName -Destination (Join-Path $stageLibs $dependencyDll.Name) -Force
        }

        if ($spec.IncludeManifest) {
            Write-EffectManifest -Path (Join-Path $stageDirectory 'manifest.json') `
                -EffectId $spec.EffectId `
                -Name $spec.Name `
                -EntryType $spec.EntryType `
                -Target $spec.Target
        }

        $packagePath = Join-Path $packageRoot $spec.FileName
        if (Test-Path -LiteralPath $packagePath) {
            Remove-Item -LiteralPath $packagePath -Force
        }

        Compress-EffectPackage -SourceDirectory $stageDirectory -PackagePath $packagePath
        Write-Host ("  - 生成：" + $spec.FileName)
    }

    Write-Host '[4/6] 回读校验包结构与 manifest...'
    foreach ($spec in $packages) {
        $packagePath = Join-Path $packageRoot $spec.FileName
        if (-not (Test-Path -LiteralPath $packagePath)) {
            throw "未生成包：$packagePath"
        }

        Test-EffectPackage -PackagePath $packagePath -Spec $spec
        Write-Host ("  - OK：" + $spec.FileName)
    }

    Write-Host '[5/6] 输出包清单：'
    foreach ($spec in $packages) {
        $packagePath = Join-Path $packageRoot $spec.FileName
        $length = (Get-Item -LiteralPath $packagePath).Length
        $entryNames = @(Get-ZipEntryNames -PackagePath $packagePath)
        Write-Host ("  {0} | effectId={1} | entryType={2} | target={3} | {4} 字节 | entries=[{5}]" -f `
            $spec.FileName, $spec.EffectId, $spec.EntryType, $spec.Target, $length, ($entryNames -join ', '))
    }

    Write-Host ''
    Write-Host ("打包完成：{0} 个特效包 → {1}" -f $packages.Count, $packageRoot) -ForegroundColor Green
}
catch {
    Write-Host ''
    Write-Host ("打包失败：" + $_.Exception.Message) -ForegroundColor Red
    $scriptExitCode = 1
    Write-Error $_
}
finally {
    Remove-PathIfExists $stagingRoot
    if (Test-Path -LiteralPath $tempZipPath) {
        Remove-Item -LiteralPath $tempZipPath -Force -ErrorAction SilentlyContinue
    }
}

exit $scriptExitCode
