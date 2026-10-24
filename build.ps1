#Requires -Version 5.1
<#
  游戏助手 · 构建脚本
  ============================
  一条命令完成：编译 -> 自检 -> 界面检查 -> 发布单文件 exe。

  用法
    powershell -ExecutionPolicy Bypass -File build.ps1
    powershell -ExecutionPolicy Bypass -File build.ps1 -Run          构建完顺便启动
    powershell -ExecutionPolicy Bypass -File build.ps1 -SkipPublish  只编译，不发布
    powershell -ExecutionPolicy Bypass -File build.ps1 -Shots        额外生成界面截图

  前置条件
    .NET SDK 9.0 或更高（dotnet --version 能输出 9.x 即可）。
    发布自包含单文件时首次需要联网拉取 win-x64 运行时包。
#>
param(
    [switch]$Run,
    [switch]$SkipPublish,
    [switch]$Shots
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'GameHelper.csproj'

# exe 自检时会把控制台切到 UTF-8 并直接按字节写 stdout。
# 如果再用 `| Write-Host` 接一道管道，PowerShell 会拿自己的 OutputEncoding
# （简体中文系统上是 GBK）去解码那些 UTF-8 字节，中文立刻变乱码。
# 所以这里两边都对齐成 UTF-8，并且调用 exe 时不接管道。
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
    & chcp.com 65001 | Out-Null
} catch {
    Write-Host '提示：无法切换控制台为 UTF-8，exe 输出的中文可能显示为乱码。'
}

# 把整个构建过程同时写一份 UTF-8 日志。
# 为什么需要：如果本脚本被外层再套一层 powershell.exe 捕获（例如某些自动化/IDE
# 终端里），外层会拿自己的 OutputEncoding（中文系统上是 GBK）去解码本脚本的
# UTF-8 输出，屏幕上就会看到「鐜妫€鏌」这类乱码——那是捕获链路的问题，不是脚本
# 的问题。有了这份日志，读文件即可定性，不必再逐字节排查。
$script:LogLines = New-Object System.Collections.Generic.List[string]

function Say([string]$text) {
    Write-Host $text
    $script:LogLines.Add($text) | Out-Null
}

function Flush-Log {
    try {
        $logPath = Join-Path $root '构建日志.txt'
        $head = @(
            '游戏助手 · 构建日志'
            ('=' * 64)
            ('时间   : ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
            ('脚本   : ' + $PSCommandPath)
            ('结果   : ' + $script:LogResult)
            ''
        )
        $all = $head + $script:LogLines
        # $true = 带 BOM，记事本 / VS Code / 旧版工具都能正确识别
        [System.IO.File]::WriteAllLines($logPath, $all, (New-Object System.Text.UTF8Encoding($true)))
    } catch {
        Write-Host ('警告：构建日志写入失败（' + $_.Exception.Message + '）')
    }
}

$script:LogResult = '未完成'

trap {
    $script:LogResult = '失败'
    Say ''
    Say ('构建失败：' + $_.Exception.Message)
    Flush-Log
    exit 1
}

function Step([string]$text) {
    Say ''
    Say ('=== ' + $text + ' ' + ('=' * [Math]::Max(0, 56 - $text.Length)))
    Say ''
}

function Invoke-Dotnet([string[]]$dotnetArgs) {
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) {
        throw ('dotnet 执行失败（退出码 ' + $LASTEXITCODE + '）：dotnet ' + ($dotnetArgs -join ' '))
    }
}

# ---------------------------------------------------------------------- 环境

Step '环境检查'
$sdk = (& dotnet --version) 2>&1
Say ('dotnet SDK : ' + $sdk)
if (-not (Test-Path $proj)) { throw ('找不到项目文件：' + $proj) }

$icon = Join-Path $root 'app.ico'
if (Test-Path $icon) {
    Say ('应用图标   : app.ico 已存在（' + (Get-Item $icon).Length + ' 字节）')
} else {
    Say '应用图标   : app.ico 不存在，将使用默认图标。'
    Say '             重新生成请运行：python tools\make_icon.py'
}

# ---------------------------------------------------------------------- 编译

Step '编译（Release）'
Invoke-Dotnet @('build', $proj, '-c', 'Release', '-v', 'minimal', '--nologo')

$devDir = Join-Path $root 'bin\Release\net9.0-windows'
$devExe = Join-Path $devDir '游戏助手.exe'
if (-not (Test-Path $devExe)) { throw ('编译产物不存在：' + $devExe) }

# ---------------------------------------------------------------------- 自检

# exe 自检时会把报告同时写到「日志」目录和 stdout（UTF-8 字节）。
# 这里必须用管道消费，不能写成 $out = & $exe ...：
# PowerShell 5.1 对 GUI 子系统程序（WinExe）的赋值捕获与 `>` 重定向都拿不到
# 输出（实测均为 0 行 / 0 字节，而同样方式调用控制台程序正常）。
# 改成管道之后能完整拿到，实测 82 行。
function Invoke-Exe([string]$path, [string[]]$exeArgs) {
    & $path @exeArgs 2>&1 | ForEach-Object { Say ([string]$_) }
}

Step '运行时自检（--selftest）'
Invoke-Exe $devExe @('--selftest')

Step '界面构建检查（--dryrun）'
Invoke-Exe $devExe @('--dryrun')

if ($Shots) {
    Step '生成界面截图（--shot）'
    $shotDir = Join-Path $devDir 'shots'
    & $devExe '--shot' $shotDir | Out-Null
    Say ('截图已生成：' + $shotDir)
}

# ---------------------------------------------------------------------- 发布

if (-not $SkipPublish) {
    Step '发布自包含单文件 exe'
    $dist = Join-Path $root 'dist'
    if (Test-Path $dist) {
        # 清理旧产物只是锦上添花：万一系统策略/安全软件拦住删除，不该让整个发布失败。
        try {
            Remove-Item -LiteralPath $dist -Recurse -Force -ErrorAction Stop
        } catch {
            Say ('警告：无法清理旧 dist 目录，继续发布（' + $_.Exception.Message + '）')
        }
    }

    Invoke-Dotnet @(
        'publish', $proj,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=none',
        '-o', $dist,
        '--nologo'
    )

    $final = Join-Path $dist '游戏助手.exe'
    if (-not (Test-Path $final)) { throw ('发布产物不存在：' + $final) }

    # 帧数依赖 PresentMon。exe 里已经内嵌了一份（运行时会自行解到
    # %LOCALAPPDATA%），但把实体文件也放进 dist，用户就能直接换成更新版本。
    # 许可必须随附 —— PresentMon 是 MIT，分发时带上声明是硬性要求。
    $distTools = Join-Path $dist 'tools'
    New-Item -ItemType Directory -Path $distTools -Force | Out-Null
    foreach ($f in @('PresentMon-x64.exe', 'PresentMon-LICENSE.txt')) {
        $srcFile = Join-Path (Join-Path $root 'tools') $f
        if (Test-Path $srcFile) {
            Copy-Item -LiteralPath $srcFile -Destination $distTools -Force
            Say ('附带 : tools\' + $f)
        } else {
            Say ('警告：缺少 ' + $f + '，帧数功能将依赖 exe 内嵌的那一份')
        }
    }

    $mb = [Math]::Round((Get-Item $final).Length / 1MB, 1)
    Say ''
    Say ('产物 : ' + $final)
    Say ('体积 : ' + $mb + ' MB（自包含，双击即用，无需安装任何运行时）')

    # 顺手放到上一级目录，和其他工具放在一起
    $parent = Split-Path $root -Parent
    $beside = Join-Path $parent '游戏助手.exe'
    Copy-Item -LiteralPath $final -Destination $beside -Force
    Say ('副本 : ' + $beside)
}

if ($Run) {
    Step '启动'
    & $devExe
}

Step '完成'
Say '提示：exe 会在自己旁边创建「日志」目录，存放运行日志、体检报告与监控 CSV。'
Say '      若该位置不可写（例如放在 Program Files），会自动改用「文档」目录。'

$script:LogResult = '成功'
Flush-Log
Write-Host ''
Write-Host ('构建日志已写入：' + (Join-Path $root '构建日志.txt'))
