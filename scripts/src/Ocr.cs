// Ocr.cs — ocr 命令的 WinRT OCR 封装（Windows.Media.Ocr）
// 说明: 对 PNG 做可选整数倍上采样后识别；返回的坐标均为【放大前】原图像素坐标，
//       由调用方叠加截图区域原点换算为屏幕绝对坐标。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace WinHarness {
    static class OcrRunner {
        // 识别 PNG 文件；upscale>=2 时先按整数倍高质量插值放大（提升小字号识别率）。
        // 返回 {engine:"<语言Tag>", lines:[{text,x,y,w,h,words:[{text,x,y,w,h}]}]}
        public static Dictionary<string, object> Recognize(string srcPng, string lang, int upscale) {
            if (upscale < 1) upscale = 1;
            var lines = new List<Dictionary<string, object>>();
            string engineTag;

            using (var src = new Bitmap(srcPng)) {
                Bitmap ocrBmp = src;
                bool ownBmp = false;
                try {
                    if (upscale > 1) {
                        ocrBmp = new Bitmap(src.Width * upscale, src.Height * upscale);
                        ownBmp = true;
                        using (var g = Graphics.FromImage(ocrBmp)) {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(src, 0, 0, ocrBmp.Width, ocrBmp.Height);
                        }
                    }
                    using (var ms = new MemoryStream()) {
                        ocrBmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        ms.Position = 0;
                        var engine = CreateEngine(lang);
                        engineTag = engine.RecognizerLanguage.LanguageTag;
                        var ras = ms.AsRandomAccessStream();
                        try {
                            var decoder = BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
                            using (var sb = decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult()) {
                                var ocr = engine.RecognizeAsync(sb).AsTask().GetAwaiter().GetResult();
                                foreach (var line in ocr.Lines) lines.Add(LineToDict(line, upscale));
                            }
                        } finally {
                            ras.Dispose();
                        }
                    }
                } finally {
                    if (ownBmp) ocrBmp.Dispose();
                }
            }

            var res = new Dictionary<string, object>();
            res["engine"] = engineTag;
            res["lines"] = lines;
            return res;
        }

        // 语言包缺失时给出可用语言列表，便于调用方改用 -Lang
        static OcrEngine CreateEngine(string lang) {
            OcrEngine engine = null;
            if (!string.IsNullOrEmpty(lang)) {
                try { engine = OcrEngine.TryCreateFromLanguage(new Language(lang)); } catch { engine = null; }
            }
            if (engine == null) engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) {
                var avail = new List<string>();
                try { foreach (var l in OcrEngine.AvailableRecognizerLanguages) avail.Add(l.LanguageTag); } catch { }
                throw new Exception("系统未安装 OCR 语言包 " + (string.IsNullOrEmpty(lang) ? "(用户配置)" : lang)
                    + "；可用: " + (avail.Count == 0 ? "无" : string.Join(", ", avail.ToArray())));
            }
            return engine;
        }

        // 行 = 各词 BoundingRect 的并集（OcrLine 本身没有矩形）
        static Dictionary<string, object> LineToDict(OcrLine line, int upscale) {
            var words = new List<Dictionary<string, object>>();
            var text = new StringBuilder();
            int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
            foreach (var w in line.Words) {
                var r = w.BoundingRect;
                int wx = Down(r.X, upscale), wy = Down(r.Y, upscale);
                int ww = Down(r.Width, upscale), wh = Down(r.Height, upscale);
                words.Add(Rect(w.Text, wx, wy, ww, wh));
                if (text.Length > 0) text.Append(' ');
                text.Append(w.Text);
                if (wx < x1) x1 = wx;
                if (wy < y1) y1 = wy;
                if (wx + ww > x2) x2 = wx + ww;
                if (wy + wh > y2) y2 = wy + wh;
            }
            if (words.Count == 0) { x1 = 0; y1 = 0; x2 = 0; y2 = 0; }
            var d = Rect(text.ToString(), x1, y1, x2 - x1, y2 - y1);
            d["words"] = words;
            return d;
        }

        static Dictionary<string, object> Rect(string text, int x, int y, int w, int h) {
            var d = new Dictionary<string, object>();
            d["text"] = text;
            d["x"] = x; d["y"] = y; d["w"] = w; d["h"] = h;
            return d;
        }

        // 放大后的坐标 → 原图坐标
        static int Down(double v, int upscale) {
            return (int)Math.Round(v / upscale, MidpointRounding.AwayFromZero);
        }
    }
}
