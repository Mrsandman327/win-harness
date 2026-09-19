// InputSim.cs — 输入模拟（鼠标点击/文本输入/按键）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WinHarness {
    static class InputSim {
        // 常用虚拟键码表
        static readonly Dictionary<string, byte> VkMap = new Dictionary<string, byte> {
            { "ENTER", 0x0D }, { "ESC", 0x1B }, { "ESCAPE", 0x1B }, { "TAB", 0x09 }, { "SPACE", 0x20 },
            { "BACKSPACE", 0x08 }, { "BKSP", 0x08 }, { "DELETE", 0x2E }, { "DEL", 0x2E }, { "INSERT", 0x2D },
            { "UP", 0x26 }, { "DOWN", 0x28 }, { "LEFT", 0x25 }, { "RIGHT", 0x27 },
            { "HOME", 0x24 }, { "END", 0x23 }, { "PGUP", 0x21 }, { "PAGEUP", 0x21 }, { "PGDN", 0x22 }, { "PAGEDOWN", 0x22 },
            { "CTRL", 0x11 }, { "CONTROL", 0x11 }, { "ALT", 0x12 }, { "SHIFT", 0x10 }, { "WIN", 0x5B },
            { "F1", 0x70 }, { "F2", 0x71 }, { "F3", 0x72 }, { "F4", 0x73 }, { "F5", 0x74 }, { "F6", 0x75 },
            { "F7", 0x76 }, { "F8", 0x77 }, { "F9", 0x78 }, { "F10", 0x79 }, { "F11", 0x7A }, { "F12", 0x7B },
        };

        // 扩展键集合（SendInput 需携带 KEYEVENTF_EXTENDEDKEY 标志）
        static readonly HashSet<byte> ExtendedKeys = new HashSet<byte> {
            0x2E, 0x2D, 0x24, 0x23, 0x21, 0x22, 0x26, 0x28, 0x25, 0x27
        };

        // 在屏幕坐标点击（支持双击/右键）
        public static KeyValuePair<int, int> ClickPoint(int x, int y, bool dbl, bool right) {
            Native.SetCursorPos(x, y);
            Thread.Sleep(350);
            uint down = right ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_LEFTDOWN;
            uint up = right ? Native.MOUSEEVENTF_RIGHTUP : Native.MOUSEEVENTF_LEFTUP;
            Native.mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            Native.mouse_event(up, 0, 0, 0, UIntPtr.Zero);
            if (dbl) {
                Thread.Sleep(120);
                Native.mouse_event(down, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(80);
                Native.mouse_event(up, 0, 0, 0, UIntPtr.Zero);
            }
            return new KeyValuePair<int, int>(x, y);
        }

        // 在指定点发出一次真实的鼠标移动事件。
        // 实现要点：用 SetCursorPos 定位（多屏下无需坐标归一化，最可靠），
        // 再发一个【零位移】的 MOUSEEVENTF_MOVE —— 目的是让应用收到 WM_MOUSEMOVE。
        // 注意不要用 MOUSEEVENTF_ABSOLUTE 自己做归一化：该标志默认只覆盖主显示器，
        // 多屏环境必须额外带 MOUSEEVENTF_VIRTUALDESK，否则坐标会被映射到错误位置。
        public static void EmitMoveAt(int x, int y) {
            Native.SetCursorPos(x, y);
            var input = new Native.INPUT {
                type = Native.INPUT_MOUSE,
                U = new Native.InputUnion {
                    mi = new Native.MOUSEINPUT {
                        dx = 0, dy = 0, mouseData = 0,
                        dwFlags = Native.MOUSEEVENTF_MOVE,
                        time = 0, dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            Native.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        static void EmitButton(bool down, bool right) {
            uint flag = right ? (down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP)
                              : (down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP);
            Native.mouse_event(flag, 0, 0, 0, UIntPtr.Zero);
        }

        // 移动光标到指定点（不点击）
        public static void MoveTo(int x, int y) {
            EmitMoveAt(x, y);
            Thread.Sleep(150);
        }

        // 拖拽：起点按下 → 分步插值移动 → 终点弹起。
        // 两个关键点：
        //  1) 移动必须发出真实移动事件，否则应用收不到 WM_MOUSEMOVE，拖拽不成立
        //  2) 分步移动（而非瞬移），覆盖系统的拖拽启动阈值 SM_CXDRAG
        public static void DragTo(int fx, int fy, int tx, int ty, int durationMs, bool right) {
            if (durationMs < 120) durationMs = 120;
            int steps = Math.Max(12, durationMs / 20);
            int delay = Math.Max(6, durationMs / steps);

            EmitMoveAt(fx, fy);
            Thread.Sleep(220);
            EmitButton(true, right);
            Thread.Sleep(170);

            // 先小幅抖动，越过拖拽启动阈值
            EmitMoveAt(fx + 4, fy + 4);
            Thread.Sleep(delay);

            for (int i = 1; i <= steps; i++) {
                int x = fx + (int)Math.Round((double)(tx - fx) * i / steps);
                int y = fy + (int)Math.Round((double)(ty - fy) * i / steps);
                EmitMoveAt(x, y);
                Thread.Sleep(delay);
            }
            Thread.Sleep(170);
            EmitMoveAt(tx, ty);
            EmitButton(false, right);
            Thread.Sleep(150);
        }

        // 滚轮：delta 为负向下滚、正向上滚；每次 120 为一"格"
        public static void Scroll(int x, int y, int delta, int times) {
            if (times < 1) times = 1;
            Native.SetCursorPos(x, y);
            Thread.Sleep(180);
            for (int i = 0; i < times; i++) {
                Native.mouse_event(Native.MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
                Thread.Sleep(80);
            }
        }

        // 输入文本（剪贴板粘贴法，支持中文/emoji；原剪贴板内容自动恢复）        // 剪贴板操作走 Win32 API（避开 WinForms/OLE 对消息泵的依赖，保证 STA 无消息循环场景可靠）。
        public static void TypeText(string text) {
            string old = null;
            try { old = ClipboardNative.GetText(); } catch { }
            ClipboardNative.SetText(text);
            Thread.Sleep(250);
            KeyPress("CTRL+V");
            Thread.Sleep(400);
            if (old != null) {
                try { ClipboardNative.SetText(old); } catch { }
            }
        }

        // 发送按键/组合键，如 "ENTER" / "CTRL+S" / "ALT+F4"（SendInput 实现）
        public static void KeyPress(string keys) {
            if (string.IsNullOrEmpty(keys)) throw new Exception("缺少 -Keys（如 ENTER / CTRL+S）");
            var parts = keys.ToUpperInvariant().Split('+').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            var codes = new List<byte>();
            foreach (var p in parts) {
                if (VkMap.ContainsKey(p)) codes.Add(VkMap[p]);
                else if (p.Length == 1) codes.Add((byte)char.ToUpperInvariant(p[0]));
                else throw new Exception("未知按键: " + p);
            }
            // 按下（顺序）
            foreach (var c in codes) { SendKey(c, false); Thread.Sleep(80); }
            Thread.Sleep(100);
            // 释放（逆序）
            for (int i = codes.Count - 1; i >= 0; i--) { SendKey(codes[i], true); Thread.Sleep(80); }
        }

        // SendInput 发送单键（自动识别扩展键）
        static void SendKey(byte vk, bool up) {
            uint flags = up ? Native.KEYEVENTF_KEYUP : 0;
            if (ExtendedKeys.Contains(vk)) flags |= Native.KEYEVENTF_EXTENDEDKEY;
            var input = new Native.INPUT {
                type = Native.INPUT_KEYBOARD,
                U = new Native.InputUnion {
                    ki = new Native.KEYBDINPUT {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            Native.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Native.INPUT)));
        }
    }

    // 截图（全屏 / 指定矩形 / 指定显示器）
    static class ScreenCap {
        // 返回 {path, x, y, w, h}：报告实际捕获矩形，供调用方把图片坐标换算回屏幕坐标。
        public static Dictionary<string, object> Capture(Native.RECT? rect, int monitor, string outPath) {
            int x, y, w, h;
            if (rect.HasValue) {
                x = rect.Value.Left; y = rect.Value.Top;
                w = rect.Value.Right - rect.Value.Left; h = rect.Value.Bottom - rect.Value.Top;
            } else if (monitor >= 0) {
                var screens = Screen.AllScreens;
                if (monitor >= screens.Length) throw new Exception("显示器索引越界: " + monitor + "（共检测到 " + screens.Length + " 台）");
                var b = screens[monitor].Bounds;
                x = b.X; y = b.Y; w = b.Width; h = b.Height;
            } else {
                // 整个虚拟桌面（横跨所有显示器），而非仅主屏
                var b = System.Windows.Forms.SystemInformation.VirtualScreen;
                x = b.X; y = b.Y; w = b.Width; h = b.Height;
            }
            if (w <= 0 || h <= 0) throw new Exception("截图区域无效: " + w + "x" + h);
            if (string.IsNullOrEmpty(outPath)) {
                outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "win-harness-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
            }
            using (var bmp = new System.Drawing.Bitmap(w, h))
            using (var g = System.Drawing.Graphics.FromImage(bmp)) {
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
                bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            var res = new Dictionary<string, object>();
            res["path"] = outPath;
            res["x"] = x; res["y"] = y; res["w"] = w; res["h"] = h;
            return res;
        }
    }

    // 显示器几何查询（供 setwindow -MoveToMonitor / -Center 使用）
    static class Screens {
        public static int Count { get { return Screen.AllScreens.Length; } }

        public static Native.RECT WorkingArea(int index) {
            var all = Screen.AllScreens;
            if (index < 0 || index >= all.Length) throw new Exception("显示器索引越界: " + index + "（共 " + all.Length + " 台）");
            var b = all[index].WorkingArea;
            return new Native.RECT { Left = b.X, Top = b.Y, Right = b.X + b.Width, Bottom = b.Y + b.Height };
        }

        // 矩形中心点落在哪台显示器上（兜底返回 0）
        public static int IndexOf(Native.RECT r) {
            int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            var all = Screen.AllScreens;
            for (int i = 0; i < all.Length; i++) {
                var b = all[i].Bounds;
                if (cx >= b.X && cx < b.X + b.Width && cy >= b.Y && cy < b.Y + b.Height) return i;
            }
            return 0;
        }
    }


    // 剪贴板（Win32 直操作，CF_UNICODETEXT；带重试）
    static class ClipboardNative {
        public static void SetText(string text) {
            Exception last = null;
            for (int i = 0; i < 5; i++) {
                if (TrySetText(text)) return;
                last = new Exception("剪贴板被其他进程占用");
                Thread.Sleep(150);
            }
            throw new Exception("设置剪贴板失败: " + (last != null ? last.Message : ""));
        }

        static bool TrySetText(string text) {
            if (!Native.OpenClipboard(IntPtr.Zero)) return false;
            try {
                Native.EmptyClipboard();
                byte[] bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
                IntPtr hMem = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                if (hMem == IntPtr.Zero) return false;
                IntPtr ptr = Native.GlobalLock(hMem);
                if (ptr == IntPtr.Zero) { Native.GlobalFree(hMem); return false; }
                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, ptr, bytes.Length);
                Native.GlobalUnlock(hMem);
                if (Native.SetClipboardData(Native.CF_UNICODETEXT, hMem) == IntPtr.Zero) {
                    Native.GlobalFree(hMem);
                    return false;
                }
                return true; // 成功后内存所有权归系统，不可释放
            } finally {
                Native.CloseClipboard();
            }
        }

        public static string GetText() {
            if (!Native.IsClipboardFormatAvailable(Native.CF_UNICODETEXT)) return null;
            if (!Native.OpenClipboard(IntPtr.Zero)) return null;
            try {
                IntPtr hMem = Native.GetClipboardData(Native.CF_UNICODETEXT);
                if (hMem == IntPtr.Zero) return null;
                IntPtr ptr = Native.GlobalLock(hMem);
                if (ptr == IntPtr.Zero) return null;
                try {
                    return System.Runtime.InteropServices.Marshal.PtrToStringUni(ptr);
                } finally {
                    Native.GlobalUnlock(hMem);
                }
            } finally {
                Native.CloseClipboard();
            }
        }
    }
}
