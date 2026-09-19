# win-harness

Windows 桌面自动化 CLI。单文件 exe，零依赖（.NET Framework 4.x 自带 `csc.exe` 编译），面向 AI agent 调用。

基于 **UI Automation**（主通道）+ **MSAA**（回退通道）+ **SendInput**（真实输入）+ **GDI**（截图）。

> 操作手册（命令参数、排障、工作流）见 [SKILL.md](SKILL.md)。本文件是技术总览：实现原理、功能、能力范围。

- 命令数：**22**
- 源码：**4 个 C# 文件** / 约 **1800 行**
- 产物：`scripts/win-harness.exe`（单文件，约 **62 KB**）

---

## 目录

- [一、技术原理](#一技术原理)
- [二、功能](#二功能)
- [三、能力范围](#三能力范围)
- [四、构建与维护](#四构建与维护)

---

## 一、技术原理

### 1.1 坐标空间统一 —— 一切的地基

进程若为 DPI-unaware，UIA 返回物理像素，而 GDI（`GetWindowRect` / `CopyFromScreen`）走系统虚拟化坐标。**两者混用会导致截图截错区域**，在多显示器 + 缩放环境下必现。

启动时（`Native.EnableDpiAwareness()`，在 `Main` 中最先执行）按能力从高到低逐级尝试声明，老系统缺新 API 会抛 `DllNotFound` / `EntryPointNotFound`，逐级回退：

| 顺序 | API | 生效标识 |
|---|---|---|
| 1 | `SetProcessDpiAwarenessContext(-4)` | `per-monitor-v2` |
| 2 | `SetProcessDpiAwareness(2)` | `per-monitor` |
| 3 | `SetProcessDPIAware()` | `system` |

生效模式记录在 `Native.DpiMode`，并随相关命令的响应返回，便于事后追溯。

### 1.2 元素定位与歧义断言 —— 核心安全机制

**要解决的问题**：子串匹配 + 多匹配静默取第一个。

真实事故：`click -Name "三"`（数字三按钮）命中了「**三角函数**」按钮，导致计算结果为 `12×789` 而非 `123×789`，**且全程无任何报错**。

**机制**（`Uia.FindSpec` + `Uia.ResolveOne`）：

- **多维 AND 匹配**：`Name`（默认子串，`-Exact` 时精确）、`AutomationId`（恒精确）、`Type`（ControlType 短名）、`ClassName`（恒精确）。一次遍历同时判定全部条件。
- **歧义即失败**：需要唯一元素时，若匹配数 > 1 **且未显式传入 `-Index`**，直接抛错并列出候选（type / name / automationId / className / rect），附收敛建议。
- **关键实现细节**：必须区分「显式传了 `-Index 0`」与「根本没传」——靠 `o.ContainsKey("Index")` 判定，无法用默认值表达。
- **窗口定位同理**：`-Window` 多候选且未给 `-WindowIndex` 时报错，列出 hwnd / pid / proc / title / rect。

`find` 命令豁免（它本就该返回多结果）。

### 1.3 点击策略：按控件类型对齐语义

`click` 的 `-Via auto`（默认）不是简单地"选一个 Pattern 调用"，而是**按控件类型对齐鼠标点击的真实语义**（`Uia.TryPattern`）：

| 控件类型 | 鼠标点击的含义 | 使用的通道 |
|---|---|---|
| ListItem / TreeItem / TabItem / DataItem | **选中** | `SelectionItemPattern.Select()` |
| CheckBox / ToggleButton | **切换** | `TogglePattern.Toggle()` |
| Button / Hyperlink / MenuItem | **激活默认动作** | `InvokePattern.Invoke()` |
| 其他 | — | 真实鼠标点击 |

> **`Invoke` 被刻意排在最后。** 对列表项而言 `InvokePattern.Invoke()` 的语义是「执行默认动作」
> （≈ **双击 → 打开**），而非「点击 → 选中」。若排在前面，一次本意是"选中文件"的 `click`
> 会**把文件打开** —— 破坏性副作用，且响应里只显示 `via=invoke`，调用方无从察觉。
>
> `ExpandCollapse` 同样**不进 auto 链**：它等价于点「展开箭头」，而非点击行本身（行点击是选中）。

需要显式激活/打开时写 `-Via invoke`；强制真实鼠标点击写 `-Via click`（`-ForceClick` 为其别名）。
响应中的 `via` 取值：`select` / `toggle` / `expand` / `invoke` / `click` / `click-fallback`（auto 下无 Pattern 可用）/ `point`。

**收益**：屏外元素可点（不需要可点击点）、不怕遮挡、免疫 DPI、直达控件语义。
实测：计算器 `123 × 789` 的 8 次点击**全部走 `invoke`**，零鼠标参与（按钮不支持 Select/Toggle，自然落位）；资源管理器列表项点击走 `select`，选中状态 `False → True` 且**未打开文件**。

### 1.4 输入模拟的三个设计决策

| 输入类型 | 机制 | 关键原因 |
|---|---|---|
| **文本** | Win32 直操作剪贴板（`OpenClipboard` / `GlobalAlloc` / `SetClipboardData`）+ `Ctrl+V`，用完**还原原剪贴板** | 避开 OLE 对消息泵的依赖，保证 STA 无消息循环场景可靠；5 次重试应对剪贴板被占用 |
| **按键** | `SendInput`，按下顺序 / 释放逆序，自动识别扩展键（方向键、Delete、Home/End 等需携带 `KEYEVENTF_EXTENDEDKEY`） | 相比 `keybd_event`，对 WinUI 应用兼容性更好 |
| **鼠标移动** | `SetCursorPos(x,y)` + **零位移**的 `MOUSEEVENTF_MOVE` 输入事件 | `SetCursorPos` 只挪光标、**不产生输入事件**；而 `MOUSEEVENTF_ABSOLUTE` 的坐标归一化**默认只覆盖主显示器**，多屏环境必须额外携带 `MOUSEEVENTF_VIRTUALDESK`，否则坐标被映射到错误位置，故放弃该路线 |

**拖拽**（`InputSim.DragTo`）的两个必要条件：

1. 移动必须发出**真实移动事件**——否则应用收不到 `WM_MOUSEMOVE`，拖拽不成立；
2. **分步插值**（`max(12, Duration/20)` 步，每步 ≥ 6ms），且起点按下后先做一次小幅抖动，以越过系统拖拽启动阈值 `SM_CXDRAG`。单次瞬移会被多数应用判定为无效拖拽。

### 1.5 MSAA 回退通道

**问题**：Chromium 系应用（Edge / Electron / WebView2 / Tauri）的 UIA 树极浅（实测 6–13 个元素）——因为 Chromium 的无障碍树是**惰性启用**的，对无条件全树遍历不激活。

**实现**（`Uia.MsaaTree` / `WalkMsaa`）：经 `oleacc.dll` 的 `AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, IID_IAccessible)` 获取 `IAccessible`，递归 `accChildCount` / `accChild(i)`，读取 `accName` / `accValue` / `accRole` / `accLocation`。

- 简单元素（`get_accChild` 返回 null）用父对象 + 子 ID 访问；完整元素则递归其自身（childId = 0）。
- 纯只读通道，失败即返回空列表，**不影响 UIA 主路径**。
- 角色名映射使用官方 `ROLE_SYSTEM_*` 常量值（注意与 UIA 的 `ControlType` 编号体系**完全不同**，不可互用）。

**`-Backend auto`（默认）**：UIA 元素数 < **15** 时自动改走 MSAA，取元素更多者，并标注 `"backend":"msaa-fallback"`。

阈值 15 的来源是实测：Win32 / WinUI / Qt 均 > 40，而 Chromium 系仅 6–13，区分度充足。

### 1.6 截图与坐标自检

- 全屏截图使用 `SystemInformation.VirtualScreen`（**整个虚拟桌面**），而非 `Screen.PrimaryScreen.Bounds`（仅主屏）。
- `-Monitor n` 可指定单个显示器。
- **始终返回实际捕获矩形** `{x, y, w, h}`，供调用方把图片坐标换算回屏幕坐标。
- **坐标自检**（`RectMismatchWarning`）：同一次调用里分别用 UIA 和 `GetWindowRect` 读取窗口矩形，差异 > 2px 就在响应中附加 `warning` 字段（不失败）。目的是让坐标异常**可见**，而不是靠猜。

### 1.7 输出协议与批处理

- **单行 JSON**：`{"ok":true,"data":...}` 或 `{"ok":false,"error":"..."}`。
- `MiniJson` 将所有非 ASCII 字符转义为 `\uXXXX`，输出纯 ASCII —— 彻底规避控制台 / 管道编码链（GBK 与 UTF-8 混杂）造成的中文损坏。
- **`do` 批处理**：复用同一个 `Dispatch` 函数在**同一进程内**执行多步，不另起进程；失败即停并报出第几步失败及原因。

---

## 二、功能

### 2.1 窗口管理

| 命令 | 说明 |
|---|---|
| `windows [-Filter 标题] [-Pid n]` | 枚举顶层窗口（hwnd / title / pid / proc / rect） |
| `focus -Window <定位>` | 激活窗口（自动还原最小化；ALT 键技巧 + `AttachThreadInput` 置前台） |
| `setwindow -Window <定位> [-X -Y -Width -Height] [-State normal\|minimize\|maximize\|restore\|hide\|show] [-MoveToMonitor n] [-Center]` | 窗口状态 / 位置 / 尺寸 / 跨屏移动 / 居中 |
| `launch -Path <exe或文档> [-Args] [-Workdir] [-WaitWindow <标题>] [-Timeout 15] [-Max]` | 启动程序或文档，可等待窗口出现 |
| `kill (-Window \| -Pid \| -Path) [-Force]` | 结束进程（默认先礼貌关闭，3s 超时强杀） |

### 2.2 元素探查

| 命令 | 说明 |
|---|---|
| `tree -Window <定位> [-Depth 3] [-Filter] [-Backend uia\|msaa\|auto]` | 元素树（扁平 JSON，含 automationId / className / rect） |
| `find -Window <定位> [条件]` | 查找元素（最多 20 个，含坐标、可点击点、value） |
| `read -Window <定位> [条件]` | 读元素文本（ValuePattern → TextPattern → Name 三级回退） |
| `getprop -Window <定位> [条件]` | 元素属性 + 支持的 Pattern 列表 + Toggle / Selection / ExpandCollapse 状态 |

### 2.3 交互操作

| 命令 | 说明 |
|---|---|
| `click -Window <定位> (-AutomationId \| -Name \| -X -Y) [-Index] [-Double] [-Right] [-Scroll] [-ForceClick]` | 点击，Pattern 优先 |
| `type -Window <定位> (-Text \| -TextB64) [-Focus] [-NoAutoFocus]` | 输入文本（剪贴板法，支持中文 / emoji） |
| `setvalue -Window <定位> [条件] (-Text)` | **直接设值**，绕过键盘，最可靠 |
| `key -Window <定位> -Keys "CTRL+S" [-AutoFocus]` | 按键 / 组合键 |
| `hover [-Window <定位>] -X -Y` | 移动光标 + 用 `FromPoint` 报告光标下元素 |
| `drag [-Window <定位>] (-FromX -FromY -ToX -ToY \| -From x,y -To y,y) [-Duration 500] [-Button left\|right]` | 拖拽（分步插值） |
| `scroll [-Window <定位>] -X -Y [-Delta -120] [-Times 1]` | 真滚轮事件 |

### 2.4 感知与流程

| 命令 | 说明 |
|---|---|
| `screenshot [-Window <定位>] [-Monitor n] [-Out <路径>]` | 截图（整个虚拟桌面 / 单显示器 / 指定窗口），返回捕获矩形 |
| `pixel -X -Y` | 取屏幕像素颜色（自绘 UI 的视觉验证手段） |
| `clipboard [-Get] \| [-Set -Text]` | 读 / 写剪贴板 |
| `wait -Window <定位> -Name <> [-Timeout 10] [-Gone]` | 等待元素出现 / 消失 |
| `do -Ops <子命令1 ; 子命令2 ; ...>` | 批处理，失败即停并定位到具体步骤 |
| `b64 -Text <>` | 文本转 base64（备用） |

### 2.5 通用定位参数

**元素**：`-Name` / `-NameB64` / `-AutomationId` / `-Type` / `-Class` / `-Exact` / `-Index`

**窗口**：标题（模糊包含）｜`pid:1234`｜`hwnd:12345`｜`-WindowIndex n`

**降级顺序**：`-AutomationId`（首选，稳定）→ `-Name -Exact` → `-Name` + `-Type` + `-Class` → `-X -Y` 坐标（最后手段，配合 `hover` 先探测）

---

## 三、能力范围

### 3.1 支持度分级定义

| 等级 | 含义 |
|---|---|
| ★★★★★ | 纯元素驱动，零截图，`AutomationId` 稳定，可无值守复现 |
| ★★★★☆ | 元素为主，偶需坐标兜底；存在已知的应用侧怪癖 |
| ★★★☆☆ | 元素 + 视觉混合，部分控件需坐标或探测 |
| ★★☆☆☆ | 坐标盲操作 + 视觉验证，无结构可依 |
| ★☆☆☆☆ | 基本不可用，应改用其他通道 |

### 3.2 各类型应用支持程度（实测）

元素数为 `tree -Depth 4` 的实际返回数量。

| 应用类型 | 代表 | 升级前 | 升级后 | backend | 等级 | 主要短板 |
|---|---|---|---|---|---|---|
| **Win32 经典控件** | 资源管理器、旧版记事本、对话框、菜单/列表/树/编辑框 | 63 | 63–67 | uia | **★★★★★** | 老式自绘 ListView 单元格 |
| **WinForms** | .NET 桌面应用 | — | — | uia | **★★★★★** | 无实质短板 |
| **WPF** | — | — | — | uia | **★★★★★** | 虚拟化列表需 `-Scroll` |
| **UWP / WinUI** | 计算器、设置、商店应用 | 41 | 41 | uia | **★★★★☆** | 合成组合键常不响应 → 改用 `setvalue` |
| **Qt** | Navicat | 46 | 46 | uia | **★★★☆☆** | QML（Qt Quick）暴露差；需应用开启无障碍 |
| **WebView2 / Electron** | OC Manager / VS Code / Clash Verge | 13 / 6 / 11 | 13 / **9** / 11 | uia / **msaa** | **★★★☆☆**（仅外壳） | 摸不到页面内容 → **用 CDP** |
| **Chromium（浏览器）** | Edge | 7 | **17** | msaa-fallback | **★★★☆☆**（仅外壳） | 同上 |
| **自绘 UI** | 微信 4.0、部分国产软件、CAD、游戏启动器 | 3 | 3 | uia | **★★☆☆☆** | UIA 几乎为空，只剩截图 + `pixel` + `hover` 探测 + 坐标 |
| **XAML（系统组件）** | 任务管理器 | 2 | 2 | uia | **★☆☆☆☆** | 几乎无有效暴露 |
| **Office** | Word / Excel / PPT | — | — | uia | **★★☆☆☆** | 仅 Ribbon / 对话框可控，**文档正文不可控** |
| **DirectX 全屏游戏** | — | — | — | — | **☆☆☆☆☆** | 架构上不支持 |

### 3.3 三堵墙（本工具做不到，请改用其他通道）

| 场景 | 正确通道 | 原因 |
|---|---|---|
| **浏览器 / WebView 页面内部** | **CDP（browser-harness）** | UIA 与 MSAA 都摸不到网页正文。MSAA 实测只多挖出**浏览器外壳**（窗口按钮、标签栏），不触及 DOM |
| **Office 文档正文** | **COM / VBA** | UIA 只能摸到 Ribbon 和对话框；Word 正文、Excel 单元格表需要 Office 对象模型 |
| **DirectX 全屏游戏** | **不支持** | 独占全屏下 GDI 截图为黑屏，需 DXGI Desktop Duplication（不同技术栈）；`SendInput` 合成事件会被 raw input 忽略，反作弊还会拦截 |

### 3.4 与 browser-harness 的分工

| 场景 | 用哪个 |
|---|---|
| 浏览器页面内操作（登录态、JS 渲染、DOM 精细操作） | **browser-harness**（CDP） |
| 桌面窗口级操作（任何 exe 的窗口、按钮、输入框） | **win-harness**（UIA） |
| WebView2 / Electron 应用 | 页面内精细操作 → browser-harness；窗口 / 启动 / 对话框 → win-harness |

### 3.5 已验证的局限

1. **WinUI 应用组合键失效** —— Win11 记事本上 `CTRL+A` / `CTRL+C` / `CTRL+V` 对合成输入无响应。对策：`setvalue` 直接设值 + `read` 直接读文档。
2. **纯坐标操作会被遮挡** —— `hover` / `drag` / `scroll` 作用于屏幕最顶层的窗口。实测 `hover` 在计算器按钮坐标处返回的是资源管理器文件项（有窗口盖着）。对策：传 `-Window` 先把目标置前台（已内建 `EnsureTargetForeground`）。
3. **`setwindow` 尺寸会被目标应用调整** —— 位置始终精确；尺寸可能被应用最小尺寸或 DPI 虚拟化改变（计算器请求 800×600 实得 800×762，部分窗口出现 1.5x）。**以返回的 `rect` 为准**。
4. **`do` 批处理提速仅 1.2x** —— 总耗时被各命令内部固定等待（焦点 400ms、Pattern 200ms 等）占据，进程启动只占一部分。真正收益是**一次工具调用替代多次**。
5. **MSAA 只提升数量、不保证质量** —— 多出的是容器与外壳节点，且角色精度依赖 `ROLE_SYSTEM_*` 映射表。
6. **autofocus 在宽泛窗口上不可用** —— 如资源管理器有 50 个 `Edit` 元素（列头），`-AutoFocus` 会因歧义报错。这是有意的：需用 `-Focus <名称>` 精确指定，而不是随便点一个。
7. **未实现** —— 图像模板匹配（`findimage`）、`wait -Stable`、`repl` 常驻模式、`-DryRun`。
8. **行为变更（有意）** —— 旧的宽松调用方式变严：`-Type Edit` 这类宽泛条件现在会因多匹配而报错（如资源管理器上匹配 98–142 个元素）。这是为消灭「静默操作错元素」付出的代价。

### 3.6 已修复的高危问题（避免回归）

这些问题都曾真实存在，修复过程见 git 历史；此处记录以防后人改回。

| 问题 | 曾经的后果 | 现在的行为 |
|---|---|---|
| 列表项点击走 `Invoke` | 本意「选中文件」变成**「打开文件」**（破坏性且无提示） | `Select` 排前、`Invoke` 排最后；`via` 如实汇报通道 |
| autofocus 用合成点击实现 | `type` / `key` 抢焦点时**清空列表选中状态** | 改用 UIA `SetFocus()`；`type` / `key` 默认不再干预焦点 |
| `launch -WaitWindow` 取首个同标题窗口 | 已有同名窗口时返回**错误的 hwnd / pid** | 优先等本次启动进程自己的窗口；退回时附 `warning` |
| `kill -Path` 取首个同名进程 | 可能杀掉**错误的进程** | 多进程时报错列候选，要求 `-Pid` |
| 元素 / 窗口多匹配静默取首个 | 操作到错误元素 / 窗口且无报错 | 一律报错列候选，要求 `-Index` / `-WindowIndex` |
| `MOUSEEVENTF_ABSOLUTE` 做坐标归一化 | 多屏下坐标被映射到主显示器，拖拽失效 | 改为 `SetCursorPos` + 零位移 `MOUSEEVENTF_MOVE` |
| `build.ps1` 不检查退出码 | 编译失败仍报「成功」，静默保留过期 exe | 编译前删旧产物 + 检查 `$LASTEXITCODE` |
| 负数坐标被解析成「无值开关」 | 左/上副屏上 `click`/`hover`/`drag`/`scroll`/`pixel` **全部不可用** | 参数解析器区分「选项名」与「负数值」；`click` 不再用 `值 >= 0` 判断是否走坐标路径 |
| 坐标丢失伪装成元素歧义 | 报「匹配到 N 个元素（无过滤条件）」，把「参数丢了」伪装成「元素歧义」，排障方向被带偏 | 坐标缺失/无效时直接报坐标错误；无任何条件时明确要求给坐标或元素条件 |
| `setwindow` 的 `applied` 与 `rect` 自相矛盾 | 同一响应出现两个矛盾矩形（跨 DPI 移动后 1.5x 差异），易被误读 | `applied` 只列动作名；新增 `requested_rect` / `geometry_note` / `monitor_index` |

> **反思 A（语义层面）**：列表项点击与 autofocus 两条是**同一个思维错误的两种表现** ——
> 把「控件语义动作」等同于「用户操作」。`Invoke` ≠ 点击，`SetFocus` ≠ 点击。
> 凡是引入会调用控件语义动作的改动，必须逐控件类型核对它到底做了什么，
> 并覆盖**列表项**这类非按钮场景的测试。

> **反思 B（深度层面）**：负数坐标那条有**两层** bug —— 解析器把 `-1281` 当选项，
> 以及 `click` 用 `px >= 0` 判断是否走坐标路径（`-1` 既当「未提供」哨兵又当合法值）。
> **只修被报告的那一层是不够的**，修完第一层会立刻撞上第二层。
> 收到 bug 报告时应顺着数据流把同一路径上的判断全部复查一遍，而不是只修报错点。

### 3.7 回归状态

| 项 | 结果 |
|---|---|
| 11 个原命令向后兼容 | 通过 |
| 四档样本（Win32 / WinUI / Chromium / 自绘） | 通过 |
| 11 个新命令冒烟 | 通过 |
| 歧义护栏（元素 / 窗口） | 通过 |
| 总计 | **21 / 24**（3 个 FAIL 均为歧义护栏正确拦截，非缺陷） |

---

## 四、构建与维护

### 文件结构

```
win-harness/
├── README.md              本文件（技术总览）
├── SKILL.md               操作手册（命令参数、工作流、排障）
└── scripts/
    ├── win-harness.exe         编译产物（单文件，运行时不依赖 PowerShell）
    ├── build.ps1               编译脚本
    └── src/
        ├── Native.cs           P/Invoke：窗口 / 鼠标键盘 / DPI / 剪贴板 / MSAA
        ├── Uia.cs              UIA 封装 + FindSpec/ResolveOne + MSAA 遍历
        ├── InputSim.cs         SendInput 鼠标键盘 / 剪贴板 / 截图 / 显示器几何
        └── WinHarness.cs       CLI 主程序 + 命令分发 + 批处理 + MiniJson
```

### 编译

```powershell
powershell -ExecutionPolicy Bypass -File "scripts\build.ps1"
```

使用 .NET Framework 自带 `csc.exe`（路径自动探测 Framework64 / Framework），**无需安装任何工具链**。源文件先转 UTF-8 BOM（csc 依 BOM 识别编码，保证中文字面量正确）。

编译脚本会先删除旧产物、再检查 `$LASTEXITCODE` —— 避免编译失败却报「成功」并静默保留过期 exe。

### 设计取舍记录

| 决策 | 理由 |
|---|---|
| 编译为 exe 而非 PowerShell 脚本 | 脚本内嵌 C# P/Invoke 会触发 AMSI 启发式误拦（`ScriptContainedMaliciousContent`）；二进制不经脚本扫描 |
| JSON 输出 `\uXXXX` 转义 | 规避控制台 / 管道编码链（GBK 与 UTF-8 混杂）造成的中文损坏，任何链路下无损 |
| 只用 .NET Framework 内置程序集 | 保持零依赖、单文件；`Accessibility.dll` 缺失时自动跳过 MSAA 通道，不影响其余功能 |
| 歧义一律报错而非告警 | 「静默操作错元素」比「报错」代价高得多（曾导致计算结果错误且无感知） |
