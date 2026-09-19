// WinHarness.cs — win-harness 主程序（Windows 桌面自动化 CLI）
// 用法: win-harness.exe <命令> [-参数 值]...
// 输出: 统一 JSON 到 stdout（{"ok":true,"data":...} 或 {"ok":false,"error":"..."}）
// 说明: 编译为 exe 运行（不经 PowerShell 文本扫描，规避 AMSI 启发式误拦）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace WinHarness {
    class Program {
        [STAThread] // 剪贴板操作要求 STA
        static int Main(string[] args) {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            Native.EnableDpiAwareness(); // 必须最先执行：统一 UIA / GDI 的坐标空间

            try {
                if (args.Length == 0) return PrintHelp();
                string cmd = args[0].ToLowerInvariant();
                if (cmd == "help" || cmd == "--help" || cmd == "-h") return PrintHelp();

            var opts = ParseArgs(args, 1);

            if (cmd == "b64") {
                Console.WriteLine(B64.Encode(GetText(opts)));
                return 0;
            }

                object data = Dispatch(cmd, opts);

                var result = new Dictionary<string, object>();
                result["ok"] = true;
                result["data"] = data;
                Console.WriteLine(MiniJson.Serialize(result));
                return 0;
            } catch (Exception ex) {
                var result = new Dictionary<string, object>();
                result["ok"] = false;
                result["error"] = ex.Message;
                Console.WriteLine(MiniJson.Serialize(result));
                return 1;
            }
        }

        // 命令分发（do 批处理复用同一入口，不另起进程）
        static object Dispatch(string cmd, Dictionary<string, string> opts) {
            switch (cmd) {
                case "windows": return CmdWindows(opts);
                case "tree": return CmdTree(opts);
                case "find": return CmdFind(opts);
                case "click": return CmdClick(opts);
                case "type": return CmdType(opts);
                case "setvalue": return CmdSetValue(opts);
                case "key": return CmdKey(opts);
                case "screenshot": return CmdScreenshot(opts);
                case "wait": return CmdWait(opts);
                case "focus": return CmdFocus(opts);
                case "setwindow": return CmdSetWindow(opts);
                case "launch": return CmdLaunch(opts);
                case "kill": return CmdKill(opts);
                case "hover": return CmdHover(opts);
                case "drag": return CmdDrag(opts);
                case "scroll": return CmdScroll(opts);
                case "clipboard": return CmdClipboard(opts);
                case "read": return CmdRead(opts);
                case "getprop": return CmdGetProp(opts);
                case "pixel": return CmdPixel(opts);
                case "do": return CmdDo(opts);
                default: throw new Exception("未知命令: " + cmd + "（运行 win-harness.exe help 查看用法）");
            }
        }

        // 帮助以 JSON 输出（中文经 \uXXXX 转义，避免控制台编码链损坏）
        static int PrintHelp() {
            var res = new Dictionary<string, object>();
            res["ok"] = true;
            res["data"] = new Dictionary<string, object> { { "help", HelpText } };
            Console.WriteLine(MiniJson.Serialize(res));
            return 0;
        }

        // ============ 参数解析 ============

        // 判断 token 是「选项名」还是「值」。
        //
        // 关键：负数（如 -1281）以 '-' 开头，但它是【值】不是选项。
        // 若不做这个区分，位于左/上副屏（负坐标区域）的窗口，其 -X/-Y 会被解析成"无值开关"，
        // 导致 click / hover / drag / scroll / pixel 在负坐标显示器上全部不可用。
        static bool LooksLikeOption(string s) {
            if (string.IsNullOrEmpty(s) || s[0] != '-') return false;   // 不以 - 开头 → 是值
            string body = s.Substring(1);
            if (body.Length == 0) return true;                          // 单个 "-" → 选项
            if (body[0] == '-') return true;                            // "--xxx" → 长选项
            double tmp;
            if (double.TryParse(body, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out tmp)) return false;  // -123 / -1.5 → 是值
            return true;
        }

        // 解析 "-Key value"、开关 "-Flag"（记为 ""）、以及 "-Key=value" 形式。
        // "=" 形式是给「值本身以 - 开头」的字符串留的逃生口，例如 launch -Args=-Dfoo。
        static Dictionary<string, string> ParseArgs(string[] args, int start) {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = start; i < args.Length; i++) {
                string a = args[i];
                if (!LooksLikeOption(a)) continue;                      // 游离的值：忽略

                int eq = a.IndexOf('=');
                if (eq > 1) {
                    d[a.Substring(1, eq - 1)] = a.Substring(eq + 1);
                    continue;
                }

                string key = a.TrimStart('-');
                if (i + 1 < args.Length && !LooksLikeOption(args[i + 1])) {
                    d[key] = args[i + 1];
                    i++;
                } else {
                    d[key] = "";
                }
            }
            return d;
        }

        // 必填整数参数：区分「未提供」与「提供了但无效」，避免误导性报错。
        // 接受负数（负坐标显示器必需）。
        static int RequireInt(Dictionary<string, string> o, string key, string usage) {
            string v;
            if (!o.TryGetValue(key, out v)) throw new Exception("缺少 -" + key + "（" + usage + "）");
            int n;
            if (!int.TryParse(v, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out n)) {
                throw new Exception("-" + key + " 值无效: \"" + v + "\"（需要整数，可为负数。若值本身以 - 开头，可用 -" + key + "=值 形式）");
            }
            return n;
        }

        static string Opt(Dictionary<string, string> o, string key, string def = null) {
            string v;
            return o.TryGetValue(key, out v) ? v : def;
        }

        static int OptInt(Dictionary<string, string> o, string key, int def) {
            string v;
            if (o.TryGetValue(key, out v) && v.Length > 0) { int n; if (int.TryParse(v, out n)) return n; }
            return def;
        }

        static bool Flag(Dictionary<string, string> o, string key) { return o.ContainsKey(key); }

        // 文本参数：优先 *B64 变体（中文可靠传递），否则用普通值
        static string GetText(Dictionary<string, string> o, string plainKey = "Text", string b64Key = "TextB64") {
            string b64 = Opt(o, b64Key);
            if (!string.IsNullOrEmpty(b64)) return B64.Decode(b64);
            return Opt(o, plainKey);
        }

        static string GetName(Dictionary<string, string> o) {
            string b64 = Opt(o, "NameB64");
            if (!string.IsNullOrEmpty(b64)) return B64.Decode(b64);
            return Opt(o, "Name");
        }

        // 元素查找条件（-Name/-NameB64 / -AutomationId / -Type / -Class / -Exact 多维 AND）
        static Uia.FindSpec GetSpec(Dictionary<string, string> o) {
            var s = new Uia.FindSpec();
            s.Name = GetName(o);
            s.AutomationId = Opt(o, "AutomationId");
            s.Type = Opt(o, "Type");
            s.ClassName = Opt(o, "Class");
            s.Exact = Flag(o, "Exact");
            return s;
        }

        // 元素索引：必须区分"显式传入 -Index n"与"未传（默认 0）"——歧义检测依赖此区别
        static int GetIndex(Dictionary<string, string> o, out bool explicitIndex) {
            explicitIndex = o.ContainsKey("Index");
            return OptInt(o, "Index", 0);
        }

        // 窗口定位（-WindowIndex 用于消解同标题多窗口；未指定则多候选报错）
        static AutomationElement ResolveWin(Dictionary<string, string> o) {
            bool explicitIndex = o.ContainsKey("WindowIndex");
            return Uia.ResolveWindow(Opt(o, "Window"), OptInt(o, "WindowIndex", -1), explicitIndex);
        }

        // ============ 命令实现 ============

        static object CmdWindows(Dictionary<string, string> o) {
            return Uia.GetWindows(Opt(o, "Filter"), OptInt(o, "Pid", OptInt(o, "ProcessId", 0)));
        }

        static object CmdTree(Dictionary<string, string> o) {
            var win = ResolveWin(o);
            int depth = OptInt(o, "Depth", 3);
            int max = OptInt(o, "Max", 300);
            string filter = Opt(o, "Filter");
            string backend = (Opt(o, "Backend", "auto") ?? "auto").ToLowerInvariant();
            long hwnd = win.Current.NativeWindowHandle;

            List<Dictionary<string, object>> elements;
            string used;

            if (backend == "msaa") {
                elements = Uia.MsaaTree(new IntPtr(hwnd), depth, max);
                used = "msaa";
            } else {
                elements = Uia.GetTree(win, depth, filter, max);
                used = "uia";
                // auto：UIA 树过浅时（Chromium 系典型只有个位数元素）自动试 MSAA，取元素更多者。
                // 阈值 15 来自实测：Win32/WinUI/Qt 均 >40，而 Chromium/Electron/WebView2 仅 6-13。
                if (backend == "auto" && elements.Count < 15) {
                    var msaa = Uia.MsaaTree(new IntPtr(hwnd), depth, max);
                    if (msaa.Count > elements.Count) {
                        elements = msaa;
                        used = "msaa-fallback";
                    }
                }
            }

            var res = new Dictionary<string, object>();
            res["window"] = win.Current.Name;
            res["hwnd"] = win.Current.NativeWindowHandle;
            res["backend"] = used;
            res["count"] = elements.Count;
            res["elements"] = elements;
            return res;
        }

        static object CmdFind(Dictionary<string, string> o) {
            var win = ResolveWin(o);
            var matches = Uia.FindEx(win, GetSpec(o));
            var list = new List<Dictionary<string, object>>();
            foreach (var m in matches.Take(20)) {
                var pt = new System.Windows.Point();
                bool clickable = m.TryGetClickablePoint(out pt);
                var r = m.Current.BoundingRectangle;
                var item = new Dictionary<string, object>();
                item["type"] = Uia.TypeName(m);
                item["name"] = m.Current.Name;
                try { item["automationId"] = m.Current.AutomationId; } catch { item["automationId"] = null; }
                try { item["className"] = m.Current.ClassName; } catch { item["className"] = null; }
                item["x"] = Uia.F(r.X); item["y"] = Uia.F(r.Y);
                item["w"] = Uia.F(r.Width); item["h"] = Uia.F(r.Height);
                item["cx"] = clickable ? (int)pt.X : -1;
                item["cy"] = clickable ? (int)pt.Y : -1;
                item["clickable"] = clickable;
                string val = Uia.GetValue(m);
                if (val != null) item["value"] = val;
                list.Add(item);
            }
            var res = new Dictionary<string, object>();
            res["window"] = win.Current.Name;
            res["count"] = matches.Count;
            res["matches"] = list;
            return res;
        }

        static object CmdClick(Dictionary<string, string> o) {
            var win = ResolveWin(o);
            Uia.FocusWindow(win);
            bool dbl = Flag(o, "Double");
            bool right = Flag(o, "Right");

            // 坐标模式：显式给了 -X/-Y 就【必须】走坐标路径。
            // 不能用 "值 >= 0" 判断是否走坐标 —— 负坐标（左/上副屏）是合法值，
            // 旧实现用 -1 同时当"未提供"的哨兵和合法值，导致负坐标在逻辑上被排除。
            bool hasX = o.ContainsKey("X"), hasY = o.ContainsKey("Y");
            if (hasX || hasY) {
                if (!hasX || !hasY) throw new Exception("坐标不完整：-X 与 -Y 必须成对提供");
                int px = RequireInt(o, "X", "屏幕 X 坐标，可为负数");
                int py = RequireInt(o, "Y", "屏幕 Y 坐标，可为负数");
                var p = InputSim.ClickPoint(px, py, dbl, right);
                var resPt = new Dictionary<string, object>();
                resPt["clicked"] = "point";
                resPt["via"] = "point";
                resPt["x"] = p.Key; resPt["y"] = p.Value;
                return resPt;
            }

            // 元素模式：必须至少给一个定位条件。
            // 否则会掉进"无过滤条件查找"并报「匹配到 N 个元素」，把"参数丢了"伪装成"元素歧义"，
            // 排障方向会被彻底带偏（历史真实故障）。
            var spec = GetSpec(o);
            if (spec.IsEmpty) {
                throw new Exception("需要指定 -X -Y 坐标，或元素条件（-Name / -NameB64 / -AutomationId / -Type / -Class）");
            }
            bool idxExplicit;
            int idx = GetIndex(o, out idxExplicit);
            var el = Uia.ResolveOne(win, spec, idx, idxExplicit);
            if (Flag(o, "Scroll")) Uia.ScrollIntoView(el);

            // 点击通道：-Via auto|click|select|toggle|invoke|expand
            //   auto（默认）：按控件类型对齐鼠标点击语义（选中 → 切换 → 激活），见 Uia.TryPattern
            //   click        ：强制真实鼠标点击（最贴近"点击"字面语义，但需要可点击点）
            // 双击/右键语义与 Pattern 不对应，一律走鼠标。
            string viaMode = (Opt(o, "Via", "auto") ?? "auto").ToLowerInvariant();
            if (Flag(o, "ForceClick")) viaMode = "click";

            string via = null;
            if (viaMode != "click" && !dbl && !right) {
                via = Uia.TryPattern(el, viaMode);
                if (via == null && (viaMode != "auto")) {
                    throw new Exception("元素不支持 -Via " + viaMode + " 通道: " + Uia.Describe(el));
                }
            }
            if (via == null) {
                var pt = new System.Windows.Point();
                if (!el.TryGetClickablePoint(out pt)) throw new Exception("元素没有可点击点（可能在屏外或不可见）。可尝试 -Scroll，或改用 -X -Y 坐标点击。元素: " + Uia.Describe(el));
                InputSim.ClickPoint((int)pt.X, (int)pt.Y, dbl, right);
                var resMouse = new Dictionary<string, object>();
                resMouse["clicked"] = "element";
                resMouse["via"] = (viaMode == "click" ? "click" : "click-fallback");
                resMouse["element"] = Uia.Describe(el);
                resMouse["x"] = (int)pt.X; resMouse["y"] = (int)pt.Y;
                return resMouse;
            }
            var resPat = new Dictionary<string, object>();
            resPat["clicked"] = "element";
            resPat["via"] = via;
            resPat["element"] = Uia.Describe(el);
            return resPat;
        }

        // 置键盘焦点到可输入元素。
        //
        // 关键：用 UIA 原生的 SetFocus()，而不是"合成点击"。
        // 旧实现用 InputSim.ClickPoint 点一下编辑区来抢焦点 —— 这一下点击除置焦点外还会
        // 清空列表等其他控件的选中状态，副作用不可见却破坏调用方已建立的状态。
        //
        // SetFocus 失败时【不退回点击】，而是明确报错：静默的副作用比报错代价更高。
        // 多候选时按与 ResolveOne 相同的规则报错列候选，不擅自取第一个。
        static Dictionary<string, object> ApplyAutoFocus(Dictionary<string, string> o, AutomationElement win) {
            var spec = new Uia.FindSpec();
            string focusName = Opt(o, "Focus");
            if (!string.IsNullOrEmpty(focusName)) {
                spec.Name = focusName;
                spec.Type = Opt(o, "FocusType");
            } else {
                spec.Type = "Document";                                     // 优先文档区（浏览器/富文本）
                if (Uia.FindEx(win, spec).Count == 0) spec.Type = "Edit";   // 其次编辑框
            }

            var matches = Uia.FindEx(win, spec);
            if (matches.Count == 0) {
                var none = new Dictionary<string, object>();
                none["ok"] = false;
                none["reason"] = "未找到可聚焦元素（条件: " + spec.ToString() + "）";
                return none;
            }

            bool idxExplicit = o.ContainsKey("FocusIndex");
            int idx = OptInt(o, "FocusIndex", 0);
            if (matches.Count > 1 && !idxExplicit) {
                var sb = new StringBuilder();
                sb.Append("可聚焦元素匹配到 ").Append(matches.Count).Append(" 个（条件: ").Append(spec.ToString()).Append("），未指定 -FocusIndex。候选:\n");
                for (int i = 0; i < Math.Min(matches.Count, 10); i++) {
                    sb.Append("  [").Append(i).Append("] ").Append(Uia.Describe(matches[i])).Append("\n");
                }
                sb.Append("提示: 加 -FocusIndex <n>，或用 -Focus <名称> 精确指定");
                throw new Exception(sb.ToString());
            }
            if (idx < 0 || idx >= matches.Count) throw new Exception("-FocusIndex " + idx + " 越界（匹配数 " + matches.Count + "）");
            var target = matches[idx];

            try {
                target.SetFocus();
                Thread.Sleep(300);
            } catch (Exception ex) {
                throw new Exception("SetFocus 失败，已中止以避免退回合成点击产生副作用（可用 -Focus 指定元素，或去掉 -AutoFocus 自行管理焦点）: " + ex.Message);
            }

            var res = new Dictionary<string, object>();
            res["ok"] = true;
            res["via"] = "uia-setfocus";
            res["element"] = Uia.Describe(target);
            bool hasFocus = false;
            try { hasFocus = target.Current.HasKeyboardFocus; } catch { }
            res["hasKeyboardFocus"] = hasFocus;
            if (!hasFocus) {
                res["warning"] = "SetFocus 已调用但 HasKeyboardFocus=false，后续输入可能未落到该元素；建议用 -Focus <名称> 精确指定";
            }
            return res;
        }

        static object CmdType(Dictionary<string, string> o) {
            var win = ResolveWin(o);
            if (!Uia.FocusWindow(win)) throw new Exception("无法将目标窗口置前台（输入已取消，避免误发到其他窗口）");

            // 焦点默认【不】干预。旧版默认自动聚焦且用合成点击实现，会清空列表选中状态。
            // 显式 -AutoFocus 或 -Focus <名称> 才启用；-NoAutoFocus 优先级更高（兼容旧参数）。
            bool autoFocus = (Flag(o, "AutoFocus") || !string.IsNullOrEmpty(Opt(o, "Focus"))) && !Flag(o, "NoAutoFocus");
            var focusInfo = autoFocus ? ApplyAutoFocus(o, win) : null;

            string text = GetText(o);
            if (string.IsNullOrEmpty(text)) throw new Exception("缺少 -Text 或 -TextB64");
            InputSim.TypeText(text);
            var res = new Dictionary<string, object>();
            res["typed"] = text.Length;
            if (focusInfo != null) res["focus"] = focusInfo;
            return res;
        }

        static object CmdSetValue(Dictionary<string, string> o) {
            var win = ResolveWin(o);
            bool idxExplicit;
            int idx = GetIndex(o, out idxExplicit);
            var el = Uia.ResolveOne(win, GetSpec(o), idx, idxExplicit);
            string val = GetText(o);
            if (val == null) val = "";
            Uia.SetValue(el, val);
            var res = new Dictionary<string, object>();
            res["set"] = val.Length;
            res["name"] = el.Current.Name;
            res["element"] = Uia.Describe(el);
            return res;
        }

        static object CmdKey(Dictionary<string, string> o) {
            var win = ResolveWin(o);
            if (!Uia.FocusWindow(win)) throw new Exception("无法将目标窗口置前台（按键已取消，避免误发到其他窗口）");

            // 焦点默认不干预；显式 -AutoFocus / -Focus 才启用（见 ApplyAutoFocus 的说明）
            bool autoFocus = (Flag(o, "AutoFocus") || !string.IsNullOrEmpty(Opt(o, "Focus"))) && !Flag(o, "NoAutoFocus");
            var focusInfo = autoFocus ? ApplyAutoFocus(o, win) : null;

            string keys = Opt(o, "Keys");
            if (string.IsNullOrEmpty(keys)) throw new Exception("缺少 -Keys（如 ENTER / CTRL+S）");
            InputSim.KeyPress(keys);
            var res = new Dictionary<string, object>();
            res["keys"] = keys;
            if (focusInfo != null) res["focus"] = focusInfo;
            return res;
        }

        static object CmdScreenshot(Dictionary<string, string> o) {
            string outPath = Opt(o, "Out");
            string winSpec = Opt(o, "Window");
            int monitor = OptInt(o, "Monitor", -1);
            Dictionary<string, object> res;
            string warning = null;
            if (!string.IsNullOrEmpty(winSpec)) {
                var win = ResolveWin(o);
                Native.RECT r;
                Native.GetWindowRect(new IntPtr(win.Current.NativeWindowHandle), out r);
                warning = RectMismatchWarning(win, r);
                res = ScreenCap.Capture(r, -1, outPath);
            } else {
                res = ScreenCap.Capture(null, monitor, outPath);
            }
            if (warning != null) res["warning"] = warning;
            res["dpi_awareness"] = Native.DpiMode;
            res["monitors"] = Screens.Count;
            return res;
        }

        // 坐标空间自检：UIA 与 Win32 对同一窗口的矩形读数若不吻合，说明坐标空间不一致
        // （历史故障：多显示器 + 缩放环境下截图截错区域）。只告警，不失败。
        static string RectMismatchWarning(AutomationElement win, Native.RECT wr) {
            var r = win.Current.BoundingRectangle;
            int ux = Uia.F(r.X), uy = Uia.F(r.Y), uw = Uia.F(r.Width), uh = Uia.F(r.Height);
            int wx = wr.Left, wy = wr.Top, ww = wr.Right - wr.Left, wh = wr.Bottom - wr.Top;
            if (Math.Abs(ux - wx) <= 2 && Math.Abs(uy - wy) <= 2 && Math.Abs(uw - ww) <= 2 && Math.Abs(uh - wh) <= 2) return null;
            return string.Format("UIA rect ({0},{1},{2},{3}) != GetWindowRect ({4},{5},{6},{7})；坐标空间可能不一致（截图/点击坐标需换算）",
                ux, uy, uw, uh, wx, wy, ww, wh);
        }

        static object CmdWait(Dictionary<string, string> o) {
            var win = ResolveWin(o);
            var spec = GetSpec(o);
            string name = spec.ToString();
            int timeout = OptInt(o, "Timeout", 10);
            bool gone = Flag(o, "Gone");
            var deadline = DateTime.Now.AddSeconds(timeout);
            while (DateTime.Now < deadline) {
                var matches = Uia.FindEx(win, spec);
                bool found = matches.Count > 0;
                if (gone && !found) { var res = new Dictionary<string, object>(); res["gone"] = true; res["name"] = name; return res; }
                if (!gone && found) return Uia.Info(matches[0]);
                Thread.Sleep(300);
            }
            throw new Exception(gone ? "等待超时：元素仍然存在 (" + name + ")" : "等待超时：元素未出现 (" + name + ")");
        }

        static object CmdFocus(Dictionary<string, string> o) {
            var win = ResolveWin(o);
            bool ok = Uia.FocusWindow(win);
            var res = new Dictionary<string, object>();
            res["focused"] = win.Current.Name;
            res["hwnd"] = win.Current.NativeWindowHandle;
            res["success"] = ok;
            return res;
        }

        // 窗口几何控制：状态 / 位置 / 尺寸 / 跨屏移动 / 居中
        static object CmdSetWindow(Dictionary<string, string> o) {
            var win = ResolveWin(o);
            var h = new IntPtr(win.Current.NativeWindowHandle);
            var applied = new List<string>();

            // 1) 状态
            string state = Opt(o, "State");
            if (!string.IsNullOrEmpty(state)) {
                int cmd;
                switch (state.ToLowerInvariant()) {
                    case "normal": cmd = Native.SW_NORMAL; break;
                    case "minimize": case "min": cmd = Native.SW_MINIMIZE; break;
                    case "maximize": case "max": cmd = Native.SW_MAXIMIZE; break;
                    case "restore": cmd = Native.SW_RESTORE; break;
                    case "hide": cmd = Native.SW_HIDE; break;
                    case "show": cmd = Native.SW_SHOW; break;
                    default: throw new Exception("未知 -State: " + state + "（可用 normal|minimize|maximize|restore|hide|show）");
                }
                Native.ShowWindow(h, cmd);
                applied.Add("state=" + state);
                Thread.Sleep(400);
            }

            // 2) 位置 / 尺寸（未给出的维度保持现状）
            int rx = OptInt(o, "X", int.MinValue);
            int ry = OptInt(o, "Y", int.MinValue);
            int rw = OptInt(o, "Width", int.MinValue);
            int rh = OptInt(o, "Height", int.MinValue);
            int reqX = int.MinValue, reqY = int.MinValue, reqW = int.MinValue, reqH = int.MinValue;
            if (rx != int.MinValue || ry != int.MinValue || rw != int.MinValue || rh != int.MinValue) {
                Native.RECT cr;
                Native.GetWindowRect(h, out cr);
                int nx = rx != int.MinValue ? rx : cr.Left;
                int ny = ry != int.MinValue ? ry : cr.Top;
                int nw = rw != int.MinValue ? rw : cr.Right - cr.Left;
                int nh = rh != int.MinValue ? rh : cr.Bottom - cr.Top;
                if (nw <= 0 || nh <= 0) throw new Exception("无效尺寸: " + nw + "x" + nh);
                Native.SetWindowPos(h, IntPtr.Zero, nx, ny, nw, nh, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                reqX = nx; reqY = ny; reqW = nw; reqH = nh;
                applied.Add("rect");
                Thread.Sleep(300);
            }

            // 3) 跨屏移动（保持当前尺寸，落到目标显示器工作区左上角）
            int mon = OptInt(o, "MoveToMonitor", -1);
            if (mon >= 0) {
                Native.RECT cr;
                Native.GetWindowRect(h, out cr);
                var wa = Screens.WorkingArea(mon);
                int nw = cr.Right - cr.Left, nh = cr.Bottom - cr.Top;
                Native.SetWindowPos(h, IntPtr.Zero, wa.Left, wa.Top, nw, nh, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                applied.Add("moveToMonitor=" + mon);
                Thread.Sleep(300);
            }

            // 4) 在当前显示器工作区居中
            if (Flag(o, "Center")) {
                Native.RECT cr;
                Native.GetWindowRect(h, out cr);
                int ci = Screens.IndexOf(cr);
                var wa = Screens.WorkingArea(ci);
                int nw = cr.Right - cr.Left, nh = cr.Bottom - cr.Top;
                int nx = wa.Left + ((wa.Right - wa.Left) - nw) / 2;
                int ny = wa.Top + ((wa.Bottom - wa.Top) - nh) / 2;
                Native.SetWindowPos(h, IntPtr.Zero, nx, ny, nw, nh, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                applied.Add("center");
                Thread.Sleep(300);
            }

            if (applied.Count == 0) throw new Exception("没有可执行的窗口操作，请指定 -State / -X -Y / -Width -Height / -MoveToMonitor / -Center");

            // 回读（UIA 坐标）—— 这是【权威】结果
            var b = win.Current.BoundingRectangle;
            int ax = Uia.F(b.X), ay = Uia.F(b.Y), aw = Uia.F(b.Width), ah = Uia.F(b.Height);

            var res = new Dictionary<string, object>();
            res["title"] = win.Current.Name;
            res["hwnd"] = win.Current.NativeWindowHandle;
            res["applied"] = applied;                 // 已执行的动作名列表（不含几何结果，避免与 rect 冲突）
            res["rect"] = new Dictionary<string, object> {   // 执行后的实际几何，一切以它为准
                { "x", ax }, { "y", ay }, { "w", aw }, { "h", ah }
            };

            // 若请求了几何，明确区分「请求值」与「实得值」，并在不一致时解释原因。
            // 同一响应里出现两个矛盾的矩形而不加说明，会把调用方带偏（历史真实困扰）。
            if (reqX != int.MinValue) {
                res["requested_rect"] = new Dictionary<string, object> {
                    { "x", reqX }, { "y", reqY }, { "w", reqW }, { "h", reqH }
                };
                if (reqX != ax || reqY != ay || reqW != aw || reqH != ah) {
                    res["geometry_note"] = string.Format(
                        "请求 ({0},{1}) {2}x{3} 与实得 ({4},{5}) {6}x{7} 不一致 —— 常见原因：目标应用的最小尺寸限制，或跨显示器 DPI 不同导致的尺寸换算。请以 rect 为准。注意：窗口移动/改尺寸后，之前记录的屏幕坐标已失效，需重新读取 rect 或重新截图。",
                        reqX, reqY, reqW, reqH, ax, ay, aw, ah);
                }
            }

            Native.RECT wr;
            Native.GetWindowRect(h, out wr);
            res["monitor_index"] = Screens.IndexOf(wr);
            res["dpi_awareness"] = Native.DpiMode;
            return res;
        }

        // 启动程序 / 文档，可选等待其窗口出现
        static object CmdLaunch(Dictionary<string, string> o) {
            string path = Opt(o, "Path");
            if (string.IsNullOrEmpty(path)) throw new Exception("缺少 -Path（可执行文件或文档路径）");
            var psi = new ProcessStartInfo();
            psi.FileName = path;
            if (!string.IsNullOrEmpty(Opt(o, "Args"))) psi.Arguments = Opt(o, "Args");
            if (!string.IsNullOrEmpty(Opt(o, "Workdir"))) psi.WorkingDirectory = Opt(o, "Workdir");
            psi.UseShellExecute = true;

            var res = new Dictionary<string, object>();
            res["dpi_awareness"] = Native.DpiMode;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Process proc = null;
            try { proc = Process.Start(psi); } catch (Exception ex) { throw new Exception("启动失败: " + path + " — " + ex.Message); }
            res["pid"] = proc != null ? proc.Id : -1;

            string waitTitle = Opt(o, "WaitWindow");
            if (string.IsNullOrEmpty(waitTitle)) {
                res["waited_ms"] = 0;
                return res;
            }

            int timeout = OptInt(o, "Timeout", 15);
            int launchedPid = proc != null ? proc.Id : -1;
            var deadline = DateTime.Now.AddSeconds(timeout);
            Dictionary<string, object> found = null;
            string matchNote = null;

            // 优先等【本次启动进程自己】的窗口。
            // 否则若系统里已有同标题窗口，会立刻匹配到那个旧窗口并返回错误的 hwnd/pid。
            while (DateTime.Now < deadline) {
                foreach (var c in Uia.GetWindows(waitTitle, 0)) {
                    if (launchedPid > 0 && Convert.ToInt32(c["pid"]) == launchedPid) { found = c; break; }
                }
                if (found != null) break;
                Thread.Sleep(300);
            }

            // 本进程窗口始终未出现：退回任意同标题窗口，但必须告警（典型原因：应用复用已有实例）
            if (found == null) {
                var anyWins = Uia.GetWindows(waitTitle, 0);
                if (anyWins.Count > 0) {
                    found = anyWins[0];
                    matchNote = string.Format(
                        "未等到本次启动进程(pid={0})的窗口；标题匹配到 {1} 个其他窗口，已返回首个 (pid={2}, hwnd:{3})。该应用可能复用了已有实例。",
                        launchedPid, anyWins.Count, found["pid"], found["hwnd"]);
                }
            }

            res["waited_ms"] = (int)sw.ElapsedMilliseconds;
            if (found == null) throw new Exception("等待窗口超时（" + timeout + "s）：未出现标题包含 '" + waitTitle + "' 的窗口");
            if (matchNote != null) res["warning"] = matchNote;
            res["hwnd"] = found["hwnd"];
            res["title"] = found["title"];
            res["pid"] = found["pid"];
            if (Flag(o, "Max")) {
                Native.ShowWindow(new IntPtr(Convert.ToInt64(found["hwnd"])), Native.SW_MAXIMIZE);
                Thread.Sleep(400);
            }
            return res;
        }

        // 结束进程：默认先礼貌关闭，超时再强杀
        static object CmdKill(Dictionary<string, string> o) {
            int pid = OptInt(o, "Pid", 0);
            if (pid <= 0) {
                string winSpec = Opt(o, "Window");
                string path = Opt(o, "Path");
                if (!string.IsNullOrEmpty(winSpec)) {
                    var win = ResolveWin(o);
                    uint p;
                    Native.GetWindowThreadProcessId(new IntPtr(win.Current.NativeWindowHandle), out p);
                    pid = (int)p;
                } else if (!string.IsNullOrEmpty(path)) {
                    string name = Path.GetFileNameWithoutExtension(path);
                    var procs = Process.GetProcessesByName(name);
                    if (procs.Length == 0) throw new Exception("未找到进程: " + path);
                    // 与元素/窗口同一规则：多候选不擅自取第一个，列出候选并索要 -Pid
                    if (procs.Length > 1) {
                        var sb = new StringBuilder();
                        sb.Append("匹配到 ").Append(procs.Length).Append(" 个 ").Append(name).Append(" 进程，未指定 -Pid。候选:\n");
                        for (int i = 0; i < Math.Min(procs.Length, 10); i++) {
                            string t = "";
                            try { t = procs[i].MainWindowTitle; } catch { }
                            sb.Append("  [").Append(i).Append("] Pid:").Append(procs[i].Id).Append("  title=\"").Append(t).Append("\"\n");
                        }
                        if (procs.Length > 10) sb.Append("  ...（共 ").Append(procs.Length).Append(" 个）\n");
                        sb.Append("提示: 加 -Pid <n> 精确指定");
                        throw new Exception(sb.ToString());
                    }
                    pid = procs[0].Id;
                } else {
                    throw new Exception("需要 -Window / -Pid / -Path 之一");
                }
            }

            Process target;
            try { target = Process.GetProcessById(pid); }
            catch { throw new Exception("进程不存在或已退出: pid=" + pid); }

            string method;
            if (Flag(o, "Force")) {
                target.Kill();
                method = "kill";
            } else {
                target.CloseMainWindow();
                method = "close";
                if (!target.WaitForExit(3000)) { target.Kill(); method = "kill"; }
            }
            var res = new Dictionary<string, object>();
            res["killed"] = true;
            res["pid"] = pid;
            res["method"] = method;
            return res;
        }

        // 纯坐标操作前置步骤：若给了 -Window 则先把目标窗口置前台。
        // 必要性：SetCursorPos/拖拽/滚轮 作用于【屏幕坐标上最顶层的窗口】，
        // 若目标被其他窗口遮挡，操作会打到错误的窗口上（且毫无报错）。
        static void EnsureTargetForeground(Dictionary<string, string> o) {
            if (string.IsNullOrEmpty(Opt(o, "Window"))) return;
            var win = ResolveWin(o);
            Uia.FocusWindow(win);
        }

        // 光标移动；同时报告光标下的元素（对无 UIA 的自绘 UI 尤其有价值：能探测到"外壳"这一层）
        static object CmdHover(Dictionary<string, string> o) {
            int x = RequireInt(o, "X", "屏幕 X 坐标，可为负数");
            int y = RequireInt(o, "Y", "屏幕 Y 坐标，可为负数");
            EnsureTargetForeground(o);
            InputSim.MoveTo(x, y);
            var res = new Dictionary<string, object>();
            res["x"] = x; res["y"] = y;
            try {
                var el = AutomationElement.FromPoint(new System.Windows.Point(x, y));
                if (el != null) {
                    var d = new Dictionary<string, object>();
                    d["type"] = Uia.TypeName(el);
                    d["name"] = el.Current.Name;
                    try { d["automationId"] = el.Current.AutomationId; } catch { d["automationId"] = null; }
                    try { d["className"] = el.Current.ClassName; } catch { d["className"] = null; }
                    res["element"] = d;
                } else {
                    res["element"] = null;
                }
            } catch (Exception ex) {
                res["element"] = null;
                res["element_error"] = ex.Message;
            }
            return res;
        }

        // "x,y" → [x, y]
        static int[] ParsePoint(string s) {
            if (string.IsNullOrEmpty(s)) throw new Exception("坐标为空");
            var parts = s.Split(',');
            if (parts.Length != 2) throw new Exception("坐标格式应为 x,y —— 收到: " + s);
            int a, b;
            if (!int.TryParse(parts[0].Trim(), out a) || !int.TryParse(parts[1].Trim(), out b))
                throw new Exception("坐标格式应为 x,y —— 收到: " + s);
            return new int[] { a, b };
        }

        static object CmdDrag(Dictionary<string, string> o) {
            int fx = OptInt(o, "FromX", int.MinValue), fy = OptInt(o, "FromY", int.MinValue);
            int tx = OptInt(o, "ToX", int.MinValue), ty = OptInt(o, "ToY", int.MinValue);
            string fs = Opt(o, "From"), ts = Opt(o, "To");
            if (!string.IsNullOrEmpty(fs)) { var p = ParsePoint(fs); fx = p[0]; fy = p[1]; }
            if (!string.IsNullOrEmpty(ts)) { var p = ParsePoint(ts); tx = p[0]; ty = p[1]; }
            if (fx == int.MinValue || fy == int.MinValue || tx == int.MinValue || ty == int.MinValue)
                throw new Exception("需要 -FromX -FromY -ToX -ToY，或 -From x,y -To x,y");

            bool right = string.Equals(Opt(o, "Button", "left"), "right", StringComparison.OrdinalIgnoreCase);
            int dur = OptInt(o, "Duration", 500);
            EnsureTargetForeground(o);
            InputSim.DragTo(fx, fy, tx, ty, dur, right);

            var res = new Dictionary<string, object>();
            res["from"] = fx + "," + fy;
            res["to"] = tx + "," + ty;
            res["duration_ms"] = dur;
            res["button"] = right ? "right" : "left";
            return res;
        }

        static object CmdScroll(Dictionary<string, string> o) {
            int x = RequireInt(o, "X", "滚轮事件发送到光标所在位置，可为负数");
            int y = RequireInt(o, "Y", "滚轮事件发送到光标所在位置，可为负数");
            int delta = OptInt(o, "Delta", -120);
            int times = OptInt(o, "Times", 1);
            EnsureTargetForeground(o);
            InputSim.Scroll(x, y, delta, times);
            var res = new Dictionary<string, object>();
            res["x"] = x; res["y"] = y; res["delta"] = delta; res["times"] = times;
            return res;
        }

        // 读/写剪贴板；不给 -Text/-Set 即读取
        static object CmdClipboard(Dictionary<string, string> o) {
            var res = new Dictionary<string, object>();
            string text = GetText(o);
            bool setMode = Flag(o, "Set") || text != null;
            if (!setMode) {
                res["text"] = ClipboardNative.GetText();
                return res;
            }
            if (text == null) text = "";
            ClipboardNative.SetText(text);
            res["set"] = text.Length;
            return res;
        }

        // 读取元素文本：ValuePattern → TextPattern → Name 三级回退
        static object CmdRead(Dictionary<string, string> o) {
            var win = ResolveWin(o);
            bool idxExplicit;
            int idx = GetIndex(o, out idxExplicit);
            var el = Uia.ResolveOne(win, GetSpec(o), idx, idxExplicit);

            string text = null, via = null;
            try {
                var vp = (ValuePattern)el.GetCurrentPattern(ValuePattern.Pattern);
                text = vp.Current.Value; via = "value";
            } catch { }
            if (text == null) {
                try {
                    var tp = (TextPattern)el.GetCurrentPattern(TextPattern.Pattern);
                    text = tp.DocumentRange.GetText(-1); via = "text";
                } catch { }
            }
            if (text == null) { text = el.Current.Name; via = "name"; }

            var res = new Dictionary<string, object>();
            res["text"] = text;
            res["via"] = via;
            res["element"] = Uia.Describe(el);
            return res;
        }

        // 元素属性与支持的能力（Pattern）一览
        static object CmdGetProp(Dictionary<string, string> o) {
            var win = ResolveWin(o);
            bool idxExplicit;
            int idx = GetIndex(o, out idxExplicit);
            var el = Uia.ResolveOne(win, GetSpec(o), idx, idxExplicit);

            var res = new Dictionary<string, object>();
            res["element"] = Uia.Describe(el);
            try { res["controlType"] = Uia.TypeName(el); } catch { }
            try { res["isEnabled"] = el.Current.IsEnabled; } catch { }
            try { res["isOffscreen"] = el.Current.IsOffscreen; } catch { }
            try { res["isKeyboardFocusable"] = el.Current.IsKeyboardFocusable; } catch { }
            try { res["hasKeyboardFocus"] = el.Current.HasKeyboardFocus; } catch { }
            try { res["isPassword"] = el.Current.IsPassword; } catch { }
            try { res["frameworkId"] = el.Current.FrameworkId; } catch { }
            try { res["processId"] = el.Current.ProcessId; } catch { }

            var r = el.Current.BoundingRectangle;
            res["rect"] = new Dictionary<string, object> {
                { "x", Uia.F(r.X) }, { "y", Uia.F(r.Y) }, { "w", Uia.F(r.Width) }, { "h", Uia.F(r.Height) }
            };

            var pats = new List<string>();
            try {
                foreach (var p in el.GetSupportedPatterns()) {
                    pats.Add(p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", ""));
                }
            } catch { }
            res["patterns"] = pats;

            try { var tp = (TogglePattern)el.GetCurrentPattern(TogglePattern.Pattern); res["toggleState"] = tp.Current.ToggleState.ToString(); } catch { }
            try { var sp = (SelectionItemPattern)el.GetCurrentPattern(SelectionItemPattern.Pattern); res["selectionItemIsSelected"] = sp.Current.IsSelected; } catch { }
            try { var ep = (ExpandCollapsePattern)el.GetCurrentPattern(ExpandCollapsePattern.Pattern); res["expandCollapseState"] = ep.Current.ExpandCollapseState.ToString(); } catch { }
            return res;
        }

        // 取色（自绘 UI 的视觉验证手段）
        static object CmdPixel(Dictionary<string, string> o) {            int x = RequireInt(o, "X", "屏幕 X 坐标，可为负数");
            int y = RequireInt(o, "Y", "屏幕 Y 坐标，可为负数");
            using (var bmp = new System.Drawing.Bitmap(1, 1))
            using (var g = System.Drawing.Graphics.FromImage(bmp)) {
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(1, 1));
                var c = bmp.GetPixel(0, 0);
                var res = new Dictionary<string, object>();
                res["x"] = x; res["y"] = y;
                res["r"] = (int)c.R; res["g"] = (int)c.G; res["b"] = (int)c.B;
                res["hex"] = string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
                return res;
            }
        }

        // 批处理：一次进程执行多步子命令（每步子命令本来要各自启动一次 .NET 进程，约 150-300ms）。
        // 例: do -Ops "focus -Window 计算器 ; click -AutomationId num1Button ; key -Keys ENTER"
        static object CmdDo(Dictionary<string, string> o) {
            string ops = Opt(o, "Ops");
            if (string.IsNullOrEmpty(ops)) throw new Exception("需要 -Ops（多个子命令用 ; 分隔）");

            var steps = new List<Dictionary<string, object>>();
            foreach (string raw in ops.Split(';')) {
                string seg = raw.Trim();
                if (seg.Length == 0) continue;

                string[] parts = Tokenize(seg);
                if (parts.Length == 0) continue;
                string sub = parts[0].ToLowerInvariant();
                var subOpts = ParseArgs(parts, 1);

                var step = new Dictionary<string, object>();
                step["op"] = seg;
                try {
                    step["ok"] = true;
                    step["data"] = Dispatch(sub, subOpts);
                    steps.Add(step);
                } catch (Exception ex) {
                    step["ok"] = false;
                    step["error"] = ex.Message;
                    steps.Add(step);
                    throw new Exception(string.Format("批处理第 {0} 步失败: {1} — {2}", steps.Count, seg, ex.Message));
                }
            }

            var res = new Dictionary<string, object>();
            res["count"] = steps.Count;
            res["steps"] = steps;
            return res;
        }

        // 分词：按空白切分，支持双引号包裹含空格的参数值
        static string[] Tokenize(string s) {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool inQuote = false;
            foreach (char c in s) {
                if (c == '"') { inQuote = !inQuote; continue; }
                if (!inQuote && char.IsWhiteSpace(c)) {
                    if (sb.Length > 0) { list.Add(sb.ToString()); sb.Length = 0; }
                } else {
                    sb.Append(c);
                }
            }
            if (sb.Length > 0) list.Add(sb.ToString());
            return list.ToArray();
        }

        // ============ 帮助 ============

        const string HelpText = @"win-harness — Windows 桌面自动化（UIA + 真实输入 + 截图）

命令:
  windows   [-Filter <标题包含>] [-Pid <n>]              枚举顶层窗口
  tree      -Window <定位> [-Depth 3] [-Filter <文本>]    导出元素树(扁平)
  find      -Window <定位> [-Name <>|-NameB64 <>] [-Type Button]   查找元素(含可点击点)
  click     -Window <定位> (-Name <>|-NameB64 <>| -X n -Y n) [-Index 0] [-Double] [-Right] [-Scroll]
            [-Via auto|click|select|toggle|invoke|expand] [-ForceClick]
  type      -Window <定位> (-Text <>|-TextB64 <>) [-AutoFocus] [-Focus <>] [-NoAutoFocus]
  setvalue  -Window <定位> [-Name <>|-Type Edit] (-Text <>)  直接设置元素值(UIA 原生,绕过键盘)
  key       -Window <定位> -Keys ""CTRL+S"" [-AutoFocus] [-Focus <>]  发送按键
  screenshot [-Window <定位>] [-Monitor <n>] [-Out <路径>] 截图(默认整个虚拟桌面/窗口/指定显示器)
  wait      -Window <定位> -Name <> [-Timeout 10] [-Gone] 等待元素出现/消失
  focus     -Window <定位>                                激活窗口
  setwindow -Window <定位> [-X n] [-Y n] [-Width n] [-Height n]
            [-State normal|minimize|maximize|restore|hide|show]
            [-MoveToMonitor <n>] [-Center]                窗口状态/位置/尺寸/跨屏移动/居中
  launch    -Path <exe或文档> [-Args <>] [-Workdir <>]
            [-WaitWindow <标题包含>] [-Timeout 15] [-Max]   启动程序(可等待窗口出现)
  kill      (-Window <定位> |-Pid <n> |-Path <exe>) [-Force] 结束进程(默认先礼貌关闭再强杀)
  hover     -X <n> -Y <n>                                 移动光标，并报告光标下元素
  drag      (-FromX -FromY -ToX -ToY | -From x,y -To x,y) [-Duration 500] [-Button left|right]
                                                          拖拽(分步插值移动，非瞬移)
  scroll    -X <n> -Y <n> [-Delta -120] [-Times 1]         鼠标滚轮(真滚轮，非 UIA 滚动)
  clipboard [-Get] | [-Set -Text <>]                      读/写剪贴板
  read      -Window <定位> [-Name <>|-AutomationId <>] [-Index n]   读元素文本(值/文本/名称)
  getprop   -Window <定位> [-Name <>|-AutomationId <>] [-Index n]   读元素属性与支持的 Pattern
  pixel     -X <n> -Y <n>                                 取屏幕像素颜色(视觉验证)
  do        -Ops <子命令1 ; 子命令2 ; ...>                 批处理，一次进程执行多步(省进程启动开销)
  b64       -Text <>                                      文本转 base64(UTF-8)

窗口定位: 标题(模糊包含) | pid:1234 | hwnd:12345
          同标题多窗口时需加 -WindowIndex <n>，否则报错并列出候选（避免操作错窗口）
元素定位: -Name <包含匹配> | -AutomationId <精确> | -Type <Button/Edit/...> | -Class <精确>
          -Exact        把 -Name 从包含匹配改为精确匹配
          -Index <n>    多匹配时显式选择第 n 个
          注意 匹配到多个元素且未给 -Index 时报错并列出候选，绝不静默取第一个
输出: UTF-8 JSON；中文参数可直接传（UTF-8），也可用 *B64 变体。
说明: 进程启动时自动声明 DPI 感知(per-monitor-v2 优先)，统一 UIA 与 GDI 坐标空间；
      screenshot 返回实际捕获矩形 {x,y,w,h}，可据此把图片坐标换算回屏幕坐标。
      click 的默认通道 auto 会按控件类型对齐鼠标点击语义：列表项走「选中」、复选类走「切换」、
      按钮类走「激活」(Invoke)。Invoke 排最后是刻意的 —— 对列表项它等价于双击(打开)，
      若排在前面会把本意是「选中」的操作变成「打开文件」。想显式打开请写 -Via invoke。
      type/key 默认不干预键盘焦点；需要时用 -AutoFocus 或 -Focus <名称>，实现走 UIA SetFocus
      （不产生鼠标点击，因此不会清空列表等其他控件的选中状态）。可聚焦元素多匹配时须用
      -FocusIndex <n> 消解，否则报错列候选。SetFocus 失败不回退点击，直接报错。
";
    }

    // ============ base64 工具 ============

    static class B64 {
        public static string Encode(string text) {
            if (string.IsNullOrEmpty(text)) return "";
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        }
        public static string Decode(string b64) {
            if (string.IsNullOrEmpty(b64)) return null;
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
    }

    // ============ JSON 序列化（ASCII 安全）============
    // 所有非 ASCII 字符转义为 \uXXXX，输出纯 ASCII，彻底规避控制台/管道编码链损坏。

    static class MiniJson {
        public static string Serialize(object o) {
            var sb = new StringBuilder();
            Write(sb, o);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object o) {
            if (o == null) { sb.Append("null"); return; }
            if (o is string) { WriteString(sb, (string)o); return; }
            if (o is bool) { sb.Append(((bool)o) ? "true" : "false"); return; }
            if (o is int || o is long || o is short || o is byte) {
                sb.Append(Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            var dict = o as IDictionary<string, object>;
            if (dict != null) {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict) {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    Write(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            var list = o as System.Collections.IEnumerable;
            if (list != null) {
                sb.Append('[');
                bool first = true;
                foreach (var item in list) {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, o.ToString());
        }

        static void WriteString(StringBuilder sb, string s) {
            sb.Append('"');
            foreach (char c in s) {
                switch (c) {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7E) {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        } else {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
