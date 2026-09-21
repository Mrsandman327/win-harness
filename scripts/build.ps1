# build.ps1 — 编译 win-harness.exe（使用 .NET Framework 自带 csc.exe，无需额外工具链）
# 修改 src/ 下的 .cs 源码后重新运行本脚本即可。
$ErrorActionPreference = 'Stop'
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc = "$fw\csc.exe"
if (-not (Test-Path $csc)) {
    $fw = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319"
    $csc = "$fw\csc.exe"
}
if (-not (Test-Path $csc)) { throw "未找到 csc.exe（需要 .NET Framework 4.x）" }

$src = Join-Path $PSScriptRoot "src"
$out = Join-Path $PSScriptRoot "win-harness.exe"

# 源文件转 UTF-8 BOM（csc 按 BOM 识别编码，保证中文字面量正确）
Get-ChildItem $src -Filter *.cs | ForEach-Object {
    $c = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($_.FullName, $c, (New-Object System.Text.UTF8Encoding $true))
}

$refs = @(
    "$fw\WPF\UIAutomationClient.dll",
    "$fw\WPF\UIAutomationTypes.dll",
    "$fw\WPF\WindowsBase.dll",
    "$fw\System.Drawing.dll",
    "$fw\System.Windows.Forms.dll",
    "$fw\System.Web.Extensions.dll"
)
# MSAA(IAccessible) 回退通道需要；若该程序集缺失则自动跳过（不影响其余功能）
$msaa = "$fw\Accessibility.dll"
if (Test-Path $msaa) { $refs += $msaa } else { Write-Output "提示: 未找到 Accessibility.dll，MSAA 回退通道将不可用" }

# WinRT 引用（ocr 命令）：Windows 元数据（自动选最新版本）+ WinRT 运行时扩展 + System.Runtime Facade
$winmdDir = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\UnionMetadata" -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d+(\.\d+)+$' } |
    Sort-Object { [version]$_.Name } |
    Select-Object -Last 1
$winmd = if ($winmdDir) { Join-Path $winmdDir.FullName "Windows.winmd" } else { $null }
$winrt = "$fw\System.Runtime.WindowsRuntime.dll"
# winmd 引用了 System.Runtime 契约（System.Attribute 等），需补 Facade 引用，否则 CS0012
$sysrt = Get-ChildItem "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework" -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^v4\.\d' } |
    Sort-Object { [version]$_.Name.Substring(1) } |
    Select-Object -Last 1 |
    ForEach-Object { Join-Path $_.FullName "Facades\System.Runtime.dll" }
if (-not ($sysrt -and (Test-Path $sysrt))) { $sysrt = "$fw\System.Runtime.dll" }
if ($winmd -and (Test-Path $winmd) -and (Test-Path $winrt) -and (Test-Path $sysrt)) {
    $refs += $winmd
    $refs += $winrt
    $refs += $sysrt
    Write-Output "WinRT 引用: $winmd | $winrt | $sysrt"
} else {
    throw "ocr 命令需要 WinRT 引用：Windows Kits\10\UnionMetadata\*\Windows.winmd 与 $winrt 与 $sysrt（缺失时应移除 Ocr.cs 及 ocr 命令）"
}
$refArgs = $refs | ForEach-Object { "/r:$_" }

# 先删旧产物：编译失败时不会留下"看似成功"的过期 exe
if (Test-Path $out) { Remove-Item $out -Force }

& $csc /nologo /target:exe /out:$out @refArgs "$src\Native.cs" "$src\Uia.cs" "$src\InputSim.cs" "$src\Ocr.cs" "$src\WinHarness.cs"
if ($LASTEXITCODE -ne 0) { throw "编译失败（csc 退出码 $LASTEXITCODE）" }
if (-not (Test-Path $out)) { throw "编译失败：未生成 $out" }
Write-Output "编译成功: $out"
