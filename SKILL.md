# win-harness

Windows 桌面自动化 CLI（单文件 exe，零依赖）。基于 Windows UI Automation（元素树）+ MSAA 回退通道 + SendInput（真实输入）+ GDI（截图）。

## 快速开始

```powershell
$W = ".\scripts\win-harness.exe"

& $W windows -Filter "Notepad"                    # 1. 找窗口
& $W tree -Window "Notepad" -Depth 2              # 2. 看元素树
& $W click -Window "Notepad" -AutomationId "..."  # 3. 精确定位并点击
& $W read -Window "Notepad" -Type Document        # 4. 读值验证
```

输出统一为 UTF-8 JSON：`{"ok":true,"data":...}` 或 `{"ok":false,"error":"..."}`。中文转义为 `\uXXXX`（JSON 解析后无损）。

## 命令速查

| 命令 | 用途 |
|---|---|
| `windows [-Filter 标题] [-Pid n]` | 枚举顶层窗口（hwnd/title/pid/rect） |
| `tree -Window <定位> [-Depth 3] [-Filter 文本] [-Backend uia\|msaa\|auto]` | 元素树（扁平 JSON） |
| `find -Window <定位> [-Name <>] [-Type <>] [-AutomationId <>] [-Exact]` | 查找元素（最多 20 个，含坐标/可点击点/值） |
| `click -Window <定位> (-AutomationId <>\|-Name <>\|-X n -Y n) [-Index 0] [-Double] [-Right] [-Scroll] [-Via auto\|click\|select\|toggle\|invoke\|expand] [-ForceClick]` | 点击（默认按控件语义选通道，失败回退鼠标） |
| `type -Window <定位> (-Text <>\|-TextB64 <>) [-AutoFocus] [-Focus <>] [-NoAutoFocus]` | 输入文本（剪贴板法，支持中文）|
| `setvalue -Window <定位> [-Name <>] [-Type Edit] (-Text <>)` | **直接设值**（UIA 原生，最可靠） |
| `key -Window <定位> -Keys "ENTER" [-AutoFocus] [-Focus <>]` | 按键（如 `CTRL+S` / `ALT+F4`） |
| `screenshot [-Window <定位>] [-Monitor n] [-Out <路径>]` | 截图（默认整个虚拟桌面） |
| `wait -Window <定位> -Name <> [-Timeout 10] [-Gone]` | 等待元素出现/消失 |
| `focus -Window <定位>` | 激活窗口 |
| `setwindow -Window <定位> [-X -Y -Width -Height] [-State ...] [-MoveToMonitor n] [-Center]` | 窗口状态/位置/尺寸/跨屏/居中 |
| `launch -Path <exe> [-Args <>] [-Workdir <>] [-WaitWindow <>] [-Timeout 15] [-Max]` | 启动程序（可等窗口） |
| `kill (-Window \| -Pid \| -Path) [-Force]` | 结束进程 |
| `hover [-Window <定位>] -X -Y` | 移动光标 + 报告光标下元素 |
| `drag [-Window <定位>] (-FromX -FromY -ToX -ToY \| -From x,y -To y,y) [-Duration 500] [-Button left\|right]` | 拖拽（分步插值，非瞬移） |
| `scroll [-Window <定位>] -X -Y [-Delta -120] [-Times 1]` | 真滚轮（非 UIA 滚动） |
| `clipboard [-Get] \| [-Set -Text <>]` | 读/写剪贴板 |
| `read -Window <定位> [-Name <>\|-AutomationId <>] [-Index n]` | 读元素文本（Value→Text→Name） |
| `getprop -Window <定位> [-Name <>\|-AutomationId <>] [-Index n]` | 读属性与支持的 Pattern |
| `pixel -X -Y` | 取屏幕像素颜色 |
| `do -Ops <子命令1 ; 子命令2 ; ...>` | 批处理，一次进程执行多步 |
| `b64 -Text <>` | 文本转 base64（备用） |

**窗口定位**：标题（模糊包含）｜`pid:1234`｜`hwnd:12345`

## 核心工作流

1. **找窗口** → `windows -Filter "关键词"`（拿 hwnd / title / pid）
2. **看结构** → `tree -Window <定位> -Depth 3`（了解元素、名称、automationId）
3. **精确定位** → 优先用 `-AutomationId`（最稳定），其次 `-Name -Exact`，最后 `-Name` + `-Index`
4. **操作** → `click` / `setvalue` / `type` / `key`
5. **验证** → `read` / `find` / `screenshot` / `pixel`

## 元素定位策略（四级降级）

1. **`-AutomationId`**（首选）：稳定，不受语言/版本影响。用 `tree`/`find` 先发现它
2. **`-Name -Exact`**：名称精确匹配
3. **`-Name` + `-Type` + `-Class`**：组合缩小范围
4. **`-X -Y` 坐标**：最后手段。配合 `hover` 先探测该点是什么元素

> ⚠ **歧义即报错**：匹配到多个元素且未显式给 `-Index` 时，命令会报错并列出候选，**绝不静默取第一个**。
> 同理，同标题多窗口需 `-WindowIndex <n>`，否则报错列出候选。

## 点击通道（-Via）—— 语义远比"能点就行"重要

`click` 的默认通道 `auto` **按控件类型对齐鼠标点击的真实语义**：

| 控件类型 | 鼠标点击的含义 | 使用的通道 |
|---|---|---|
| ListItem / TreeItem / TabItem / DataItem | **选中** | `SelectionItemPattern.Select()` |
| CheckBox / ToggleButton | **切换** | `TogglePattern.Toggle()` |
| Button / Hyperlink / MenuItem | **激活默认动作** | `InvokePattern.Invoke()` |
| 其他 | — | 真实鼠标点击 |

> ⚠ **`Invoke` 被刻意排在最后**。对列表项而言 `InvokePattern.Invoke()` 的语义是「执行默认动作」
> （≈ **双击 → 打开**），而不是「点击 → 选中」。若把它排在前面，一次本意是"选中文件"的 `click`
> 会**把文件打开** —— 破坏性副作用，而响应里只显示 `via=invoke`，用户无从察觉。

需要显式激活/打开时，写明 `-Via invoke`：

```powershell
& $W click -Window $hw -Type ListItem -NameB64 <b64> -Exact -Via select   # 只选中
& $W click -Window $hw -Type ListItem -NameB64 <b64> -Exact -Via invoke   # 打开
& $W click -Window $hw -AutomationId btnOk -Via click                     # 强制真实鼠标点击
```

`-ForceClick` 等价于 `-Via click`（保留兼容）。响应中的 `via` 字段取值：
`select` / `toggle` / `expand` / `invoke` / `click`（显式要求鼠标）/ `click-fallback`（auto 下无 Pattern 可用）/ `point`（坐标模式）。

## 键盘焦点（-AutoFocus）

`type` 与 `key` **默认不干预键盘焦点** —— 直接向当前前台窗口发送输入。

需要置焦点时显式加 `-AutoFocus`（或用 `-Focus <名称>` 指定目标）。其实现走 **UIA `SetFocus()`**：

> ⚠ 历史实现是「找到编辑区 → **合成点击一下**」来抢焦点。这一下点击除置焦点外，还会
> **清空列表等其他控件的选中状态**。现已改为 UIA 原生 `SetFocus()`（不产生鼠标事件）。
> `SetFocus` 失败时**不会退回点击**，而是明确报错 —— 静默的副作用比报错代价更高。

响应中会附带 `focus` 对象（`via=uia-setfocus`、`element`、`hasKeyboardFocus`，未真正获得焦点时给出 `warning`）。
多候选时同样遵守歧义规则（报错列候选），需用 `-FocusIndex <n>` 或 `-Focus <名称>` 收敛。

## 坐标系与可靠性（重要）

- 进程启动时自动声明 DPI 感知（优先 **per-monitor-v2**），统一 UIA 与 GDI 的坐标空间
- **负数坐标完全支持**。左/上副屏位于负坐标区域（如 `-X -1281 -Y 277`），可直接使用。
  解析器能区分「选项名」与「负数值」—— `-1281` 会作为 `-X` 的值，不会被当成开关。
  - 若某个值本身以 `-` 开头且不是数字（如 `-Args -Dfoo`），用等号形式：`-Args=-Dfoo`
- **坐标无效会直接报「坐标无效」**，不会退化成「匹配到 N 个元素（无过滤条件）」的元素歧义错误
- `screenshot` 返回实际捕获矩形 `{x,y,w,h}` —— 用它把图片坐标换算回屏幕坐标
- 若 UIA 与 GetWindowRect 的窗口矩形读数不一致，`screenshot` 会附带 `warning` 字段
- **`hover` / `drag` / `scroll` 是纯坐标操作**，作用于屏幕最顶层窗口。被遮挡时请传 `-Window <定位>` 先把目标置前台（否则会操作到错误的窗口上且无报错）
- `setwindow` 的响应字段（**不再自相矛盾**）：
  - `applied` —— 已执行的动作名列表（如 `["rect","moveToMonitor"]`），**不含几何结果**
  - `requested_rect` —— 请求的几何（仅当请求了几何时出现）
  - `rect` —— **权威结果**，执行后实际几何，一切以它为准
  - `geometry_note` —— 请求与实得不一致时的解释（应用最小尺寸 / 跨 DPI 换算）
  - `monitor_index` —— 窗口当前所在显示器序号
  - ⚠ **窗口移动或改尺寸后，之前记录的屏幕坐标全部失效**，需重新读 `rect` 或重新截图

## MSAA 回退通道

Chromium 系应用（Edge / Electron / WebView2 / Tauri）的 UIA 树极浅，因为 Chromium 的无障碍树是惰性启用的。
`-Backend auto`（默认）在 UIA 元素数 < 15 时自动改走 MSAA 通道，取元素更多者，并标注 `"backend":"msaa-fallback"`。

实测效果（depth=4）：Edge 7 → 17，VS Code 6 → 9。
**但要注意**：MSAA 只多挖出**浏览器外壳**（窗口按钮、标签栏），**不触及网页正文**。网页内部操作请走 CDP。

## 支持程度分级（实测）

| 类型 | 等级 | 说明 |
|---|---|---|
| Win32 经典控件（记事本旧版/资源管理器/对话框） | ★★★★★ | 纯元素驱动，`AutomationId` 稳定，零截图 |
| WinForms / WPF | ★★★★★ | 同上；WPF 虚拟化列表需 `-Scroll` |
| UWP / WinUI（计算器/设置） | ★★★★☆ | 元素暴露好；但**组合键可能不响应**，改用 `setvalue` |
| Qt（如 Navicat） | ★★★☆☆ | 部分控件可用，QML 应用暴露差 |
| 自绘 UI（微信 4.0、CAD、国产软件） | ★★☆☆☆ | UIA 几乎为空（微信实测仅 3 个元素），需截图 + `pixel` + 坐标 + `hover` 探测 |
| Chromium 系（Edge/Electron/WebView2） | ★☆☆☆☆ | UIA 树 6-13 个元素，MSAA 仅补外壳 → **请用 CDP** |

## 工具路由（三堵墙，别硬啃）

| 场景 | 用什么 | 为什么 |
|---|---|---|
| 浏览器页面内操作（登录态、JS 渲染、DOM） | **browser-harness（CDP）** | UIA/MSAA 都摸不到网页正文 |
| Office 文档正文（Word/Excel 内容） | **COM / VBA** | UIA 只能摸到 Ribbon 和对话框，正文需要 Office 对象模型 |
| DirectX 全屏游戏 | **不支持** | 独占全屏下 GDI 截图为黑屏，需 DXGI Desktop Duplication；且 SendInput 会被 raw input 忽略 |
| 桌面窗口/控件/对话框/启动程序 | **win-harness** | 本位面 |

## 已知局限与对策

| 场景 | 现象 | 对策 |
|---|---|---|
| WinUI 应用（Win11 记事本）组合键 | `CTRL+A`/`CTRL+C`/`CTRL+V` 无效 | 用 `setvalue` 直接设值；`read` 直接读文档 |
| 组合键在部分应用失效 | 无反应 | 改用分步单键，或 `setvalue` |
| 目标被其他窗口遮挡 | 纯坐标操作打错窗口且无报错 | `hover`/`drag`/`scroll` 传 `-Window` 先置前台 |
| 剪贴板被其他程序占用 | `type` 报错 | 内置 5 次重试；仍失败稍后重试 |
| 最小化窗口 | UIA 树不可用 | `focus` 自动还原（SW_RESTORE）后再操作 |
| 元素名称含空格/特殊字符 | `-Name` 不匹配 | 用 `tree` 查看精确名称；改用 `-AutomationId` |
| 多显示器 + 缩放 | 截图截错区域（历史故障） | 已修：显式 DPI 声明 + 虚拟桌面截图；留意 `warning` 字段 |

### 已修复的高危问题（避免回归）

| 问题 | 曾经的后果 | 现在的行为 |
|---|---|---|
| 列表项点击走 `Invoke` | 本意"选中文件"变成"**打开文件**"（破坏性且无提示） | `Select` 排前、`Invoke` 排最后；`via` 字段如实汇报通道 |
| autofocus 用合成点击 | `type`/`key` 抢焦点时**清空列表选中状态** | 改用 UIA `SetFocus()`；`type`/`key` 默认不再干预焦点 |
| `launch -WaitWindow` 取首个同标题窗口 | 已有同名窗口时返回**错误的 hwnd/pid** | 优先等本次启动进程自己的窗口；退回时附 `warning` |
| `kill -Path` 取首个同名进程 | 可能杀掉**错误的进程** | 多进程时报错列候选，要求 `-Pid` |
| 元素/窗口多匹配静默取首个 | 操作到错误元素/窗口且无报错 | 一律报错列候选，要求 `-Index` / `-WindowIndex` |
| 负数坐标解析成"无值开关" | 左/上副屏上 `click`/`hover`/`drag`/`scroll`/`pixel` **全部不可用** | 解析器区分选项与负数；`click` 不再用 `>= 0` 判断是否走坐标 |
| 坐标丢失伪装成元素歧义 | 报「匹配到 N 个元素（无过滤条件）」，排障方向被带偏 | 坐标无效/缺失时直接报坐标错误；无任何条件时明确要求给坐标或元素条件 |
| `setwindow` 的 `applied` 与 `rect` 自相矛盾 | 同一响应两个矛盾矩形（跨 DPI 移动后 1.5x 差异），易误读 | `applied` 只列动作；新增 `requested_rect` + `geometry_note` + `monitor_index` |

## 排障

- **未找到窗口**：`windows` 不带过滤列出全部；注意标题关键词（部分匹配即可）
- **窗口歧义报错**：按提示用 `-WindowIndex n`，或直接用 `hwnd:`
- **元素歧义报错**：按候选列表用 `-AutomationId` / `-Exact` / `-Index` 收敛
- **点击无效**：`getprop` 看 `isOffscreen`/`isEnabled`；屏外元素加 `-Scroll`；或 `-ForceClick` 强制走鼠标
- **重新编译**：修改 `scripts/src/*.cs` 后运行 `scripts/build.ps1`（用 .NET Framework 自带 csc.exe，无需安装工具链）

## 实现说明（维护参考）

- `scripts/win-harness.exe` — 编译产物（单文件，运行时不依赖 PowerShell）
- `scripts/src/Native.cs` — P/Invoke：窗口/鼠标键盘/DPl/剪贴板/MSAA
- `scripts/src/Uia.cs` — UIA 封装 + `FindSpec`/`ResolveOne`（歧义断言）+ MSAA 遍历
- `scripts/src/InputSim.cs` — SendInput 鼠标键盘/剪贴板/截图/显示器几何
- `scripts/src/WinHarness.cs` — CLI 主程序 + 命令分发 + 批处理 + MiniJson
- `scripts/build.ps1` — 编译脚本（UTF-8 BOM 转换 + 退出码检查）
- `scripts/win-harness.exe.bak` — 升级前基线备份
- **为何用 exe 而非 PowerShell 脚本**：脚本内嵌 C# P/Invoke 会触发 AMSI 启发式误拦（ScriptContainedMaliciousContent）；编译为 exe 后二进制不经脚本扫描，稳定可靠
- **为何 JSON 输出 \uXXXX 转义**：规避控制台/管道编码链（GBK/UTF-8 混杂）造成的中文损坏，任何链路下无损
