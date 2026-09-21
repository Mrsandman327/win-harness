// Uia.cs — Windows UI Automation 封装（窗口枚举/元素树/查找/滚动/聚焦）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace WinHarness {
    static class Uia {
        // 矩形数值安全转换（NaN/Infinity → -1）
        public static int F(double v) {
            if (double.IsNaN(v) || double.IsInfinity(v)) return -1;
            return (int)v;
        }

        // 控件类型短名（ControlType.Button → Button）
        public static string TypeName(AutomationElement el) {
            try { return el.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); }
            catch { return "Unknown"; }
        }

        // 元素信息 → 字典
        public static Dictionary<string, object> Info(AutomationElement el, int depth = 0) {
            var r = el.Current.BoundingRectangle;
            var d = new Dictionary<string, object>();
            if (depth >= 0) d["depth"] = depth;
            d["type"] = TypeName(el);
            d["name"] = el.Current.Name;
            try { d["automationId"] = el.Current.AutomationId; } catch { d["automationId"] = null; }
            try { d["className"] = el.Current.ClassName; } catch { d["className"] = null; }
            d["x"] = F(r.X); d["y"] = F(r.Y); d["w"] = F(r.Width); d["h"] = F(r.Height);
            return d;
        }

        // 枚举顶层窗口（UIA 方式：含 UWP/无边框窗口）
        public static List<Dictionary<string, object>> GetWindows(string filter, int pid) {
            var list = new List<Dictionary<string, object>>();
            var root = AutomationElement.RootElement;
            var wins = root.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement w in wins) {
                try {
                    if (w.Current.ControlType != ControlType.Window) continue;
                    if (pid > 0 && w.Current.ProcessId != pid) continue;
                    string title = w.Current.Name ?? "";
                    if (!string.IsNullOrEmpty(filter) && !title.Contains(filter)) continue;
                    var r = w.Current.BoundingRectangle;
                    string procName = "";
                    try { procName = Process.GetProcessById(w.Current.ProcessId).ProcessName; } catch { }
                    var item = new Dictionary<string, object>();
                    item["hwnd"] = w.Current.NativeWindowHandle;
                    item["title"] = title;
                    item["pid"] = w.Current.ProcessId;
                    item["proc"] = procName;
                    item["x"] = F(r.X); item["y"] = F(r.Y); item["w"] = F(r.Width); item["h"] = F(r.Height);
                    list.Add(item);
                } catch { }
            }
            return list;
        }

        // 解析窗口定位串：标题(模糊) | pid:1234 | hwnd:12345
        public static AutomationElement ResolveWindow(string spec) {
            return ResolveWindow(spec, -1, false);
        }

        // indexExplicit=true 时按 -WindowIndex 选择；否则多候选一律报错（避免静默操作错窗口）
        public static AutomationElement ResolveWindow(string spec, int index, bool indexExplicit) {
            if (string.IsNullOrEmpty(spec)) throw new Exception("缺少 -Window 参数（标题 / pid:123 / hwnd:123）");

            if (spec.StartsWith("hwnd:")) {
                long h;
                if (!long.TryParse(spec.Substring(5), out h)) throw new Exception("hwnd 格式错误: " + spec);
                if (h == 0 || !Native.IsWindow(new IntPtr(h)))
                    throw new Exception("hwnd " + h + " 不是有效窗口（可能已关闭）");
                try {
                    var el = AutomationElement.FromHandle(new IntPtr(h));
                    if (el == null) throw new Exception("hwnd " + h + " 无有效 UIA 元素");
                    return el;
                } catch (System.Runtime.InteropServices.COMException ce) {
                    throw new Exception("hwnd " + h + " UIA 访问失败 0x" + ((uint)ce.HResult).ToString("X8") + ": " + ce.Message);
                }
            }
            if (spec.StartsWith("pid:")) {
                int pid;
                if (!int.TryParse(spec.Substring(4), out pid)) throw new Exception("pid 格式错误: " + spec);
                var p = Process.GetProcessById(pid);
                if (p.MainWindowHandle == IntPtr.Zero) throw new Exception("进程 " + pid + " 没有主窗口");
                return AutomationElement.FromHandle(p.MainWindowHandle);
            }

            // 标题模糊匹配
            var cands = GetWindows(spec, 0);
            if (cands.Count == 0) throw new Exception("未找到标题包含 '" + spec + "' 的窗口");
            if (cands.Count > 1 && !indexExplicit) {
                var sb = new StringBuilder();
                sb.Append("标题包含 \"").Append(spec).Append("\" 的窗口有 ").Append(cands.Count).Append(" 个，未指定 -WindowIndex。候选:\n");
                int show = Math.Min(cands.Count, 10);
                for (int i = 0; i < show; i++) {
                    var c = cands[i];
                    sb.Append("  [").Append(i).Append("] hwnd:").Append(c["hwnd"])
                      .Append("  pid:").Append(c["pid"]).Append("  ")
                      .Append(c["proc"]).Append("  title=\"").Append(c["title"]).Append("\"")
                      .Append("  rect=(").Append(c["x"]).Append(",").Append(c["y"]).Append(") ")
                      .Append(c["w"]).Append("x").Append(c["h"]).Append("\n");
                }
                if (cands.Count > show) sb.Append("  ...（共 ").Append(cands.Count).Append(" 个）\n");
                sb.Append("提示: 加 -WindowIndex <n> 选择，或直接用 hwnd:<n> / pid:<n> 精确定位");
                throw new Exception(sb.ToString());
            }
            int pick = 0;
            if (indexExplicit) {
                if (index < 0 || index >= cands.Count) throw new Exception(string.Format("-WindowIndex {0} 越界（候选数 {1}）", index, cands.Count));
                pick = index;
            }
            long hwnd = Convert.ToInt64(cands[pick]["hwnd"]);
            return AutomationElement.FromHandle(new IntPtr(hwnd));
        }

        // 激活窗口：还原（若最小化）→ 多重手段置前台（ALT 键技巧 + AttachThreadInput）。
        // 校验放宽为「前台窗口与目标同进程」（WinUI 应用存在子窗口句柄差异）。
        public static bool FocusWindow(AutomationElement win) {
            var h = new IntPtr(win.Current.NativeWindowHandle);
            if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);

            uint targetPid;
            uint targetThread = Native.GetWindowThreadProcessId(h, out targetPid);
            uint currentThread = Native.GetCurrentThreadId();

            // 1) ALT 键技巧：让系统允许本次前台切换
            Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
            Native.keybd_event(0x12, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);

            // 2) AttachThreadInput + SetForegroundWindow + BringWindowToTop
            bool attached = false;
            try {
                if (targetThread != currentThread) {
                    attached = Native.AttachThreadInput(currentThread, targetThread, true);
                }
                Native.SetForegroundWindow(h);
                Native.BringWindowToTop(h);
                if (attached) Native.AttachThreadInput(currentThread, targetThread, false);
            } catch {
                if (attached) {
                    try { Native.AttachThreadInput(currentThread, targetThread, false); } catch { }
                }
            }
            Thread.Sleep(400);

            // 3) 校验：前台窗口与目标同进程即视为成功
            IntPtr fg = Native.GetForegroundWindow();
            uint fgPid;
            Native.GetWindowThreadProcessId(fg, out fgPid);
            return fgPid == targetPid;
        }

        // 导出元素树（ControlView 广度优先，深度/数量限制 + 可选过滤）
        // filter: 裸值=Name 包含；name:X / class:X / aid:X / type:X = 按字段包含
        public static List<Dictionary<string, object>> GetTree(AutomationElement root, int depth, string filter, int max) {
            var list = new List<Dictionary<string, object>>();
            var walker = TreeWalker.ControlViewWalker;
            var queue = new Queue<KeyValuePair<AutomationElement, int>>();
            queue.Enqueue(new KeyValuePair<AutomationElement, int>(root, 0));
            while (queue.Count > 0 && list.Count < max) {
                var item = queue.Dequeue();
                var el = item.Key;
                int d = item.Value;
                if (d > 0) {
                    try {
                        string name = el.Current.Name;
                        if (MatchFilter(el, name, filter)) {
                            list.Add(Info(el, d));
                        }
                    } catch { }
                }
                if (d < depth) {
                    try {
                        var child = walker.GetFirstChild(el);
                        while (child != null && list.Count < max) {
                            queue.Enqueue(new KeyValuePair<AutomationElement, int>(child, d + 1));
                            child = walker.GetNextSibling(child);
                        }
                    } catch { }
                }
            }
            return list;
        }

        // 树过滤器：裸值按 Name 包含匹配（兼容旧行为）；"字段:值" 可按字段过滤。
        // 支持 name:/class:/aid:(automationid:)/type:，未知字段一律不匹配。
        static bool MatchFilter(AutomationElement el, string name, string filter) {
            if (string.IsNullOrEmpty(filter)) return true;
            int c = filter.IndexOf(':');
            if (c <= 0) return name != null && name.Contains(filter);   // 裸值 = 仅 Name
            string field = filter.Substring(0, c).ToLowerInvariant();
            string val = filter.Substring(c + 1);
            Func<AutomationElement, string> get;
            switch (field) {
                case "name": get = e => e.Current.Name; break;
                case "class": get = e => e.Current.ClassName; break;
                case "aid": case "automationid": get = e => e.Current.AutomationId; break;
                case "type": get = e => TypeName(e); break;
                default: return false;
            }
            string s = null;
            try { s = get(el); } catch { return false; }   // UIA 可能抛 ElementNotAvailable
            return s != null && s.Contains(val);
        }

        // 查找元素：按名称（包含）/ 类型过滤（旧签名，保留兼容）
        public static List<AutomationElement> Find(AutomationElement win, string name, string type) {
            var spec = new FindSpec();
            spec.Name = name;
            spec.Type = type;
            return FindEx(win, spec);
        }

        // 元素查找条件（各维度 AND 组合；空维度忽略）
        public class FindSpec {
            public string Name;          // 默认包含匹配
            public bool Exact;           // true 时 Name 精确匹配
            public string AutomationId;  // 始终精确
            public string Type;          // ControlType 短名，如 Button
            public string ClassName;     // 始终精确

            public bool IsEmpty {
                get {
                    return string.IsNullOrEmpty(Name) && string.IsNullOrEmpty(AutomationId)
                        && string.IsNullOrEmpty(Type) && string.IsNullOrEmpty(ClassName);
                }
            }

            public override string ToString() {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(Name)) parts.Add("name" + (Exact ? "=" : "~") + "\"" + Name + "\"");
                if (!string.IsNullOrEmpty(AutomationId)) parts.Add("automationId=\"" + AutomationId + "\"");
                if (!string.IsNullOrEmpty(Type)) parts.Add("type=" + Type);
                if (!string.IsNullOrEmpty(ClassName)) parts.Add("class=\"" + ClassName + "\"");
                return parts.Count == 0 ? "(无过滤条件)" : string.Join(" ", parts.ToArray());
            }
        }

        // 多维查找：一次遍历，全部条件 AND
        public static List<AutomationElement> FindEx(AutomationElement win, FindSpec spec) {
            var matches = new List<AutomationElement>();
            var all = win.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement e in all) {
                try {
                    if (Matches(e, spec)) matches.Add(e);
                } catch { }
            }
            return matches;
        }

        static bool Matches(AutomationElement e, FindSpec spec) {
            if (!string.IsNullOrEmpty(spec.AutomationId)) {
                if (e.Current.AutomationId != spec.AutomationId) return false;
            }
            if (!string.IsNullOrEmpty(spec.Type)) {
                if (TypeName(e) != spec.Type) return false;
            }
            if (!string.IsNullOrEmpty(spec.ClassName)) {
                if (e.Current.ClassName != spec.ClassName) return false;
            }
            if (!string.IsNullOrEmpty(spec.Name)) {
                string n = e.Current.Name;
                if (n == null) return false;
                if (spec.Exact) { if (n != spec.Name) return false; }
                else { if (!n.Contains(spec.Name)) return false; }
            }
            return true;
        }

        // 元素一行摘要（用于歧义报错的候选列表）
        public static string Describe(AutomationElement e) {
            var r = e.Current.BoundingRectangle;
            string aid = null, cls = null;
            try { aid = e.Current.AutomationId; } catch { }
            try { cls = e.Current.ClassName; } catch { }
            return string.Format("{0} name=\"{1}\" automationId=\"{2}\" className=\"{3}\" rect=({4},{5},{6},{7})",
                TypeName(e), e.Current.Name, aid, cls, F(r.X), F(r.Y), F(r.Width), F(r.Height));
        }

        // 唯一匹配断言：这是"静默点错元素"的根治手段。
        // 匹配数 > 1 且未显式给 -Index 时直接报错并列出候选，绝不擅自取第 0 个。
        public static AutomationElement ResolveOne(AutomationElement win, FindSpec spec, int index, bool indexExplicit) {
            var matches = FindEx(win, spec);
            if (matches.Count == 0) throw new Exception("未找到元素: " + spec.ToString());
            if (matches.Count == 1) return matches[0];
            if (!indexExplicit) {
                var sb = new StringBuilder();
                sb.Append("匹配到 ").Append(matches.Count).Append(" 个元素（条件: ").Append(spec.ToString()).Append("），未指定 -Index。候选:\n");
                int show = Math.Min(matches.Count, 10);
                for (int i = 0; i < show; i++) {
                    sb.Append("  [").Append(i).Append("] ").Append(Describe(matches[i])).Append("\n");
                }
                if (matches.Count > show) sb.Append("  ...（共 ").Append(matches.Count).Append(" 个）\n");
                sb.Append("提示: 加 -Index <n> 选择，或用 -AutomationId / -Exact / -Type / -Class 缩小范围");
                throw new Exception(sb.ToString());
            }
            if (index < 0 || index >= matches.Count) throw new Exception(string.Format("-Index {0} 越界（匹配数 {1}）", index, matches.Count));
            return matches[index];
        }

        // 滚动到可见（虚拟列表场景）
        public static bool ScrollIntoView(AutomationElement el) {
            try {
                var sp = (ScrollItemPattern)el.GetCurrentPattern(ScrollItemPattern.Pattern);
                sp.ScrollIntoView();
                Thread.Sleep(500);
                return true;
            } catch {
                return false;
            }
        }

        // 语义优先"点击"：按控件类型对齐鼠标点击的真实语义。
        //
        //   ListItem/TreeItem/TabItem/DataItem（支持 SelectionItem）→ 点击 = 选中  → Select()
        //   CheckBox/ToggleButton（支持 Toggle）                    → 点击 = 切换  → Toggle()
        //   Button/Hyperlink/MenuItem（仅支持 Invoke）              → 点击 = 激活  → Invoke()
        //
        // ⚠ Invoke 必须排在最后。对列表项而言 InvokePattern.Invoke() 的语义是"执行默认动作"
        //   （≈ 双击 → 打开/激活），而不是"点击"。若排在前面，一次本意是"选中"的 click
        //   会把文件打开 —— 破坏性副作用，且响应里只会显示 via=invoke，用户无从察觉。
        //
        // ExpandCollapse 故意不进 auto 链：它等价于点"展开箭头"，而非点击行本身（行点击是选中）。
        // 需要展开时用 -Via expand 显式声明。
        public static string TryPattern(AutomationElement el, string via) {
            if (string.IsNullOrEmpty(via)) via = "auto";
            try { if (!el.Current.IsEnabled) return null; } catch { }

            if (via == "auto") {
                if (TrySelect(el)) return "select";
                if (TryToggle(el)) return "toggle";
                if (TryInvoke(el)) return "invoke";
                return null;
            }
            switch (via) {
                case "select": return TrySelect(el) ? "select" : null;
                case "toggle": return TryToggle(el) ? "toggle" : null;
                case "expand": return TryExpand(el) ? "expand" : null;
                case "invoke": return TryInvoke(el) ? "invoke" : null;
                default: throw new Exception("未知 -Via: " + via + "（可用 auto|click|select|toggle|invoke|expand）");
            }
        }

        static bool TrySelect(AutomationElement el) {
            try {
                var sp = (SelectionItemPattern)el.GetCurrentPattern(SelectionItemPattern.Pattern);
                sp.Select();
                Thread.Sleep(200);
                return true;
            } catch { return false; }
        }

        static bool TryToggle(AutomationElement el) {
            try {
                var tp = (TogglePattern)el.GetCurrentPattern(TogglePattern.Pattern);
                tp.Toggle();
                Thread.Sleep(200);
                return true;
            } catch { return false; }
        }

        static bool TryExpand(AutomationElement el) {
            try {
                var ep = (ExpandCollapsePattern)el.GetCurrentPattern(ExpandCollapsePattern.Pattern);
                ep.Expand();
                Thread.Sleep(200);
                return true;
            } catch { return false; }
        }

        static bool TryInvoke(AutomationElement el) {
            try {
                var ip = (InvokePattern)el.GetCurrentPattern(InvokePattern.Pattern);
                ip.Invoke();
                Thread.Sleep(200);
                return true;
            } catch { return false; }
        }

        // 读取元素当前是否被选中（用于回归验证：点击列表项应选中而非激活）
        public static bool? IsSelected(AutomationElement el) {
            try {
                var sp = (SelectionItemPattern)el.GetCurrentPattern(SelectionItemPattern.Pattern);
                return sp.Current.IsSelected;
            } catch { return null; }
        }

        // ================= MSAA(IAccessible) 回退通道 =================
        // 为什么需要：Chromium 系应用（Edge / Electron / WebView2 / Tauri）的 UIA 树实测只有
        // 6-13 个元素，因为 Chromium 的无障碍树是"惰性启用"的，对无条件全树遍历不激活；
        // 传统 MSAA 通道对 UIA 客户端请求响应更积极。MSAA 是【只读】通道，不影响 UIA 主路径。

        public static List<Dictionary<string, object>> MsaaTree(IntPtr hwnd, int maxDepth, int max) {
            var list = new List<Dictionary<string, object>>();
            object acc = null;
            Guid iid = Native.IID_IAccessible;
            try {
                int hr = Native.AccessibleObjectFromWindow(hwnd, Native.OBJID_CLIENT, ref iid, ref acc);
                if (hr != 0 || acc == null) return list;
            } catch {
                return list;
            }
            var root = acc as Accessibility.IAccessible;
            if (root == null) return list;
            WalkMsaa(root, 0, 0, maxDepth, max, list);
            return list;
        }

        static void WalkMsaa(Accessibility.IAccessible acc, object childId, int depth, int maxDepth, int max,
                             List<Dictionary<string, object>> list) {
            if (list.Count >= max) return;

            string name = null, value = null;
            int role = 0, x = 0, y = 0, w = 0, h = 0;
            try { name = acc.get_accName(childId); } catch { }
            try { value = acc.get_accValue(childId); } catch { }
            try { role = SafeRole(acc, childId); } catch { }
            try { acc.accLocation(out x, out y, out w, out h, childId); } catch { }

            if (depth > 0) {
                // 过滤纯噪声：既无名称又无可见区域的节点不返回
                bool hasArea = w > 0 && h > 0;
                if (!string.IsNullOrEmpty(name) || hasArea) {
                    var d = new Dictionary<string, object>();
                    d["depth"] = depth;
                    d["backend"] = "msaa";
                    d["type"] = MsaaRoleName(role) ?? ("MsaaRole" + role);
                    d["role"] = role;
                    d["name"] = name;
                    d["x"] = x; d["y"] = y; d["w"] = w; d["h"] = h;
                    if (value != null) d["value"] = value;
                    list.Add(d);
                }
            }

            if (depth >= maxDepth) return;

            int n = 0;
            try { n = acc.accChildCount; } catch { return; }
            for (int i = 1; i <= n && list.Count < max; i++) {
                object child = null;
                try { child = acc.get_accChild(i); } catch { }
                if (child != null) {
                    var ca = child as Accessibility.IAccessible;
                    if (ca != null) {
                        WalkMsaa(ca, 0, depth + 1, maxDepth, max, list);
                        try { Marshal.ReleaseComObject(ca); } catch { }
                    }
                } else {
                    // 简单元素：child 为 null，子 ID 由父对象承载
                    WalkMsaa(acc, i, depth + 1, maxDepth, max, list);
                }
            }
        }

        static int SafeRole(Accessibility.IAccessible acc, object childId) {
            object r = acc.get_accRole(childId);
            if (r == null) return 0;
            try { return Convert.ToInt32(r); } catch { return 0; }
        }

        // MSAA 角色名映射（ROLE_SYSTEM_* 官方常量值，勿与 UIA ControlType 混淆）
        public static string MsaaRoleName(int role) {
            switch (role) {
                case 0x01: return "titlebar";
                case 0x02: return "menubar";
                case 0x03: return "scrollbar";
                case 0x04: return "grip";
                case 0x08: return "alert";
                case 0x09: return "window";
                case 0x0A: return "client";
                case 0x0B: return "menupopup";
                case 0x0C: return "menuitem";
                case 0x0D: return "tooltip";
                case 0x0E: return "application";
                case 0x0F: return "document";
                case 0x10: return "pane";
                case 0x11: return "chart";
                case 0x12: return "dialog";
                case 0x13: return "border";
                case 0x14: return "grouping";
                case 0x15: return "separator";
                case 0x16: return "toolbar";
                case 0x17: return "statusbar";
                case 0x18: return "table";
                case 0x19: return "columnheader";
                case 0x1A: return "rowheader";
                case 0x1B: return "column";
                case 0x1C: return "row";
                case 0x1D: return "cell";
                case 0x1E: return "link";
                case 0x21: return "list";
                case 0x22: return "listitem";
                case 0x23: return "outline";
                case 0x24: return "outlineitem";
                case 0x25: return "pagetab";
                case 0x28: return "graphic";
                case 0x29: return "statictext";
                case 0x2A: return "text";
                case 0x2B: return "pushbutton";
                case 0x2C: return "checkbutton";
                case 0x2D: return "radiobutton";
                case 0x2E: return "combobox";
                case 0x30: return "progressbar";
                case 0x33: return "slider";
                case 0x34: return "spinbutton";
                case 0x3C: return "pagetablist";
                case 0x3E: return "splitbutton";
                default: return null;
            }
        }

        // 读取元素的值（ValuePattern；不支持时返回 null）
        public static string GetValue(AutomationElement el) {            try {
                var vp = (ValuePattern)el.GetCurrentPattern(ValuePattern.Pattern);
                return vp.Current.Value;
            } catch {
                return null;
            }
        }

        // 设置元素的值（ValuePattern；不支持时抛错）。绕过键盘直接改值，最可靠。
        public static void SetValue(AutomationElement el, string value) {
            var vp = (ValuePattern)el.GetCurrentPattern(ValuePattern.Pattern);
            if (vp.Current.IsReadOnly) throw new Exception("元素为只读，无法设值");
            vp.SetValue(value ?? "");
        }
    }
}
