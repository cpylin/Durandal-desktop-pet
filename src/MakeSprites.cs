// MakeSprites.cs —— 占位精灵图生成器
//
// 生成 assets/pet.png（7 行 x 8 列，单帧 144x192 逻辑像素）**和配套的 pet.json**。
// 这是"占位素材"：美术水平够用但不算精致，目的是让链路先跑通 ——
// 如果自定义素材出了问题，跑一次它就能回到一个确定可用的状态。
//
// 换成你自己的形象：走 Cutout.exe（抠图）→ SheetGen.exe（生成 sheet + 清单），
// 见 README 的『换成你自己的形象』。两者各自写自己的 pet.json，所以永远不会对不上。
//
// 布局（行）：
//   0 idle   4 帧   1 think  6 帧   2 work   6 帧
//   3 alert  6 帧   4 done   8 帧   5 sleep  4 帧
//   6 error  4 帧   （awake 复用第 4 行）
//
// 编译（C# 5，无外部依赖）：
//   csc -nologo -target:exe -out:..\bin\MakeSprites.exe MakeSprites.cs -r:...

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

class MakeSprites
{
    // 画布尺寸必须和 assets\pet.json 里写的一致（本工具自己写清单，见 WriteManifest），
    // 也和 Pet.cs 的显示尺寸一致：屏幕上是 FrameW x FrameH 逻辑像素。
    // 这里画的是矢量图形，所以像素密度取 1 就够；真实素材走 SheetGen.exe（它用 2 倍）。
    const int FrameW = 144;  // 单帧宽
    const int FrameH = 192;  // 单帧高
    const int Cols   = 8;    // 每行最多 8 帧
    const int Rows   = 7;    // 7 个状态
    const double BodyY = 0.58;  // 身体中心的纵向位置（占帧高比例）：比正中略低，上头留出装饰空间

    // ---- 调色板 ----
    static readonly Color CBodyTop  = Color.FromRgb(0x8F, 0xCB, 0xFF);
    static readonly Color CBodyBot  = Color.FromRgb(0x5A, 0x9B, 0xE8);
    static readonly Color COutline  = Color.FromRgb(0x2E, 0x5C, 0x94);
    static readonly Color CEye      = Color.FromRgb(0x1F, 0x33, 0x4D);
    static readonly Color CBlush    = Color.FromArgb(0x88, 0xFF, 0x9E, 0xAE);
    static readonly Color CAccent   = Color.FromRgb(0xFF, 0xC8, 0x4A);
    static readonly Color CLeaf     = Color.FromRgb(0x6F, 0xD0, 0x8A);
    static readonly Color CInk      = Color.FromRgb(0x33, 0x4A, 0x66);

    static void Main(string[] argv)
    {
        string outPath = (argv.Length > 0) ? argv[0] : @"..\assets\pet.png";

        int w = Cols * FrameW;
        int h = Rows * FrameH;

        DrawingVisual dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            for (int row = 0; row < Rows; row++)
            {
                int n = FramesIn(row);
                for (int f = 0; f < n; f++)
                {
                    double x = f * FrameW;
                    double y = row * FrameH;
                    // 每个状态单独裁切，避免溢出到相邻格子
                    dc.PushClip(new RectangleGeometry(new Rect(x, y, FrameW, FrameH)));
                    dc.PushTransform(new TranslateTransform(x, y));
                    DrawFrame(dc, row, f, n);
                    dc.Pop();
                    dc.Pop();
                }
            }
        }

        RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        PngBitmapEncoder enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));

        string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (dir.Length > 0 && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        using (FileStream fs = File.Create(outPath))
        {
            enc.Save(fs);
        }

        WriteManifest(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)), "pet.json"));

        Console.WriteLine("已生成 " + Path.GetFullPath(outPath) + "  (" + w + "x" + h + ", " + Cols + "x" + Rows + " 格, 单帧 " +
                          FrameW + "x" + FrameH + "px)");
    }

    // 清单由生成器自己写 —— 手改 sheet 尺寸却忘了改清单，是必然踩的坑。
    // （真实素材由 SheetGen.exe 写同样的清单，只是尺寸/状态数不同。）
    static void WriteManifest(string path)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"_说明\": \"占位素材的清单，由 MakeSprites.exe 自动生成，和 pet.png 一定对得上。\\n");
        sb.Append("           换成自己的形象请看 README 的『换成你自己的形象』。\",\n");
        sb.Append("  \"_尺寸\": \"frameWidth/frameHeight 是 sheet 里每格的像素尺寸；\\n");
        sb.Append("           sheetScale 是像素密度 —— 屏幕上显示大小 = frameWidth/sheetScale。\",\n");
        sb.Append("\n");
        sb.Append("  \"sheet\": \"pet.png\",\n");
        sb.Append("  \"frameWidth\": ").Append(FrameW).Append(",\n");
        sb.Append("  \"frameHeight\": ").Append(FrameH).Append(",\n");
        sb.Append("  \"sheetScale\": 1,\n");
        sb.Append("\n");
        sb.Append("  \"animations\": {\n");
        sb.Append("    \"idle\":  { \"row\": 0, \"frames\": 4, \"fps\": 6,  \"loop\": true  },\n");
        sb.Append("    \"think\": { \"row\": 1, \"frames\": 6, \"fps\": 8,  \"loop\": true  },\n");
        sb.Append("    \"work\":  { \"row\": 2, \"frames\": 6, \"fps\": 12, \"loop\": true  },\n");
        sb.Append("    \"alert\": { \"row\": 3, \"frames\": 6, \"fps\": 10, \"loop\": true  },\n");
        sb.Append("    \"done\":  { \"row\": 4, \"frames\": 8, \"fps\": 12, \"loop\": false },\n");
        sb.Append("    \"sleep\": { \"row\": 5, \"frames\": 4, \"fps\": 3,  \"loop\": true  },\n");
        sb.Append("    \"error\": { \"row\": 6, \"frames\": 4, \"fps\": 8,  \"loop\": false },\n");
        sb.Append("\n");
        sb.Append("    \"awake\": { \"row\": 4, \"frames\": 8, \"fps\": 10, \"loop\": false }\n");
        sb.Append("  }\n");
        sb.Append("}\n");
        string dir = Path.GetDirectoryName(path);
        if (dir.Length > 0 && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine("已写出 " + Path.GetFullPath(path));
    }

    static int FramesIn(int row)
    {
        switch (row)
        {
            case 0: return 4;  // idle
            case 1: return 6;  // think
            case 2: return 6;  // work
            case 3: return 6;  // alert
            case 4: return 8;  // done
            case 5: return 4;  // sleep
            case 6: return 4;  // error
        }
        return 1;
    }

    // 每个状态取一个相位 t（0..1），据此推导姿态
    static void DrawFrame(DrawingContext dc, int row, int f, int n)
    {
        double t = (n <= 1) ? 0 : (double)f / n;
        double bob = 0;        // 上下浮动
        double squash = 0;     // 压扁量：正数变扁，负数拉长
        double eyeOpen = 1;    // 眼睛开合 0..1
        int mouth = 0;         // 0 微笑 1 张嘴 2 平 3 波浪 4 睡
        double tilt = 0;       // 倾斜角度
        int eyeStyle = 0;      // 0 普通 1 笑眼^^ 2 ×眼
        bool sparkle = false;
        bool bubble = false;
        bool motion = false;
        bool sweat = false;
        int zs = 0;

        switch (row)
        {
            case 0: // idle —— 缓慢呼吸，第 3 帧眨眼
                bob = Math.Sin(t * Math.PI * 2) * 1.6;
                squash = Math.Sin(t * Math.PI * 2) * 0.02;
                eyeOpen = (f == 2) ? 0.12 : 1.0;
                mouth = 0;
                break;

            case 1: // think —— 左右轻微摇摆，头上冒省略号
                tilt = Math.Sin(t * Math.PI * 2) * 5.0;
                bob = Math.Sin(t * Math.PI * 4) * 1.2;
                eyeOpen = 1.0;
                mouth = 2;
                bubble = true;
                break;

            case 2: // work —— 快速抖动，专注眼，加动感线
                bob = Math.Sin(t * Math.PI * 4) * 2.6;
                squash = Math.Sin(t * Math.PI * 4) * 0.05;
                eyeOpen = 1.0;
                mouth = 2;
                motion = true;
                break;

            case 3: // alert —— 弹起，睁大眼，感叹号
                bob = -Math.Abs(Math.Sin(t * Math.PI * 2)) * 5.0;
                squash = -Math.Abs(Math.Sin(t * Math.PI * 2)) * 0.06;
                eyeOpen = 1.35;
                mouth = 1;
                bubble = true;
                break;

            case 4: // done —— 欢呼跳跃，闪星星
                bob = -Math.Abs(Math.Sin(t * Math.PI * 2)) * 7.0;
                squash = -Math.Abs(Math.Sin(t * Math.PI * 2)) * 0.08;
                eyeStyle = 1;    // ^^ 形笑眼
                mouth = 1;
                sparkle = true;
                break;

            case 5: // sleep —— 伏低，闭眼，冒 Z
                bob = 2.5;
                squash = 0.07;
                eyeStyle = 1;
                mouth = 4;
                zs = 1 + (f % 3);
                break;

            case 6: // error —— 晕头转向，冒汗
                tilt = Math.Sin(t * Math.PI * 4) * 7.0;
                bob = Math.Sin(t * Math.PI * 2) * 1.5;
                eyeStyle = 2;    // × 眼
                mouth = 3;       // 波浪嘴
                sweat = true;
                break;
        }

        dc.PushTransform(new TranslateTransform(FrameW / 2.0, FrameH * BodyY + bob));
        if (tilt != 0) dc.PushTransform(new RotateTransform(tilt, 0, 0));

        DrawBody(dc, squash);
        DrawFace(dc, eyeOpen, mouth, eyeStyle);
        DrawAntenna(dc, squash, row);

        dc.Pop();
        if (tilt != 0) dc.Pop();

        // 装饰元素画在姿态变换之外（不跟着身体旋转/浮动），
        // 但仍需平移到身体中心，否则负坐标会被格子裁掉。
        if (bubble || motion || sparkle || sweat || zs > 0)
        {
            dc.PushTransform(new TranslateTransform(FrameW / 2.0, FrameH * BodyY));
            if (bubble)  DrawBubble(dc, row, f);
            if (motion)  DrawMotion(dc, t);
            if (sparkle) DrawSparkles(dc, t);
            if (sweat)   DrawSweat(dc, t);
            if (zs > 0)  DrawZs(dc, f, zs);
            dc.Pop();
        }
    }

    static void DrawBody(DrawingContext dc, double squash)
    {
        double rx = 34 * (1 + squash);
        double ry = 30 * (1 - squash);

        // 果冻质感：上浅下深
        RadialGradientBrush body = new RadialGradientBrush();
        body.GradientOrigin = new Point(0.38, 0.28);
        body.Center = new Point(0.5, 0.45);
        body.RadiusX = 0.72;
        body.RadiusY = 0.72;
        body.GradientStops.Add(new GradientStop(CBodyTop, 0.0));
        body.GradientStops.Add(new GradientStop(CBodyBot, 1.0));

        dc.DrawEllipse(body, new Pen(new SolidColorBrush(COutline), 2.2), new Point(0, 0), rx, ry);

        // 高光
        SolidColorBrush gloss = new SolidColorBrush(Color.FromArgb(0x9A, 0xFF, 0xFF, 0xFF));
        dc.DrawEllipse(gloss, null, new Point(-rx * 0.38, -ry * 0.42), rx * 0.22, ry * 0.15);

        // 腮红
        SolidColorBrush blush = new SolidColorBrush(CBlush);
        dc.DrawEllipse(blush, null, new Point(-rx * 0.62, ry * 0.18), 6.5, 4.2);
        dc.DrawEllipse(blush, null, new Point(rx * 0.62, ry * 0.18), 6.5, 4.2);
    }

    static void DrawFace(DrawingContext dc, double eyeOpen, int mouth, int eyeStyle)
    {
        SolidColorBrush ink = new SolidColorBrush(CEye);
        double ex = 12.5;
        double ey = -4;

        if (eyeStyle == 2)
        {
            // × 眼（出错）
            Pen xp = new Pen(ink, 2.2);
            xp.StartLineCap = PenLineCap.Round;
            xp.EndLineCap = PenLineCap.Round;
            double s = 4.0;
            for (int k = 0; k < 2; k++)
            {
                double cx = (k == 0) ? -ex : ex;
                dc.DrawLine(xp, new Point(cx - s, ey - s), new Point(cx + s, ey + s));
                dc.DrawLine(xp, new Point(cx - s, ey + s), new Point(cx + s, ey - s));
            }
        }
        else if (eyeStyle == 1)
        {
            // 笑眼 ^^（用两段贝塞尔画弧）
            DrawArc(dc, new Point(-ex - 5, ey + 1), new Point(-ex, ey - 3.5), new Point(-ex + 5, ey + 1), 2.0, ink);
            DrawArc(dc, new Point(ex - 5, ey + 1), new Point(ex, ey - 3.5), new Point(ex + 5, ey + 1), 2.0, ink);
        }
        else
        {
            double r = 5.4 * Math.Min(eyeOpen, 1.6);
            dc.DrawEllipse(ink, null, new Point(-ex, ey), r, r * eyeOpen);
            dc.DrawEllipse(ink, null, new Point(ex, ey), r, r * eyeOpen);
            if (eyeOpen > 0.5)
            {
                SolidColorBrush spark = new SolidColorBrush(Colors.White);
                dc.DrawEllipse(spark, null, new Point(-ex - 1.6, ey - 1.9), 1.7, 1.7);
                dc.DrawEllipse(spark, null, new Point(ex - 1.6, ey - 1.9), 1.7, 1.7);
            }
        }

        SolidColorBrush m = new SolidColorBrush(CEye);
        double my = 12;
        switch (mouth)
        {
            case 0: // 微笑
                DrawArc(dc, new Point(-5, my - 2), new Point(0, my + 2.5), new Point(5, my - 2), 1.8, m);
                break;
            case 1: // 张嘴（欢呼）
                dc.DrawGeometry(m, null, EllipseGeo(new Point(0, my + 1), 5.0, 4.2));
                break;
            case 2: // 平嘴（专注）
                dc.DrawLine(new Pen(m, 1.8), new Point(-4.5, my), new Point(4.5, my));
                break;
            case 3: // 波浪嘴（出错，两段小弧拼成 ~）
                DrawArc(dc, new Point(-6, my + 1), new Point(-3, my - 1.6), new Point(0, my + 1), 1.7, m);
                DrawArc(dc, new Point(0, my + 1), new Point(3, my + 3.6), new Point(6, my + 1), 1.7, m);
                break;
            case 4: // 睡觉的小嘴
                dc.DrawEllipse(m, null, new Point(0, my + 1), 2.6, 3.0);
                break;
        }
    }

    // 出错时头顶的汗滴
    static void DrawSweat(DrawingContext dc, double t)
    {
        double drop = Math.Sin(t * Math.PI) * 3.0;
        double x = 24;
        double y = -30 - drop;

        StreamGeometry g = new StreamGeometry();
        using (StreamGeometryContext ctx = g.Open())
        {
            ctx.BeginFigure(new Point(x, y - 7), true, true);
            ctx.BezierTo(new Point(x + 6, y + 1), new Point(x + 5, y + 6), new Point(x, y + 6), true, false);
            ctx.BezierTo(new Point(x - 5, y + 6), new Point(x - 6, y + 1), new Point(x, y - 7), true, false);
        }
        g.Freeze();

        SolidColorBrush fill = new SolidColorBrush(Color.FromArgb(0xE0, 0x8F, 0xD4, 0xFF));
        dc.DrawGeometry(fill, new Pen(new SolidColorBrush(COutline), 1.3), g);
    }

    static void DrawAntenna(DrawingContext dc, double squash, int row)
    {
        double topY = -(30 * (1 - squash)) + 1;
        Pen stem = new Pen(new SolidColorBrush(COutline), 1.8);
        stem.StartLineCap = PenLineCap.Round;
        stem.EndLineCap = PenLineCap.Round;
        dc.DrawLine(stem, new Point(0, topY), new Point(0, topY - 11));

        Color leaf = CLeaf;
        if (row == 4) leaf = CAccent;   // 完成时叶子变金色

        dc.DrawEllipse(new SolidColorBrush(leaf), new Pen(new SolidColorBrush(COutline), 1.4),
                       new Point(0, topY - 15), 5.0, 4.0);
    }

    static void DrawBubble(DrawingContext dc, int row, int f)
    {
        SolidColorBrush bg = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
        Pen edge = new Pen(new SolidColorBrush(COutline), 1.6);
        Rect r = new Rect(24, -64, 30, 24);   // 相对身体中心：格子右上角
        dc.DrawRoundedRectangle(bg, edge, r, 7, 7);

        SolidColorBrush ink = new SolidColorBrush(CInk);
        if (row == 1)
        {
            // 省略号，逐帧点亮
            for (int i = 0; i < 3; i++)
            {
                byte a = (byte)((i <= f % 3) ? 0xFF : 0x55);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(a, CInk.R, CInk.G, CInk.B)), null,
                               new Point(r.Left + 8 + i * 7, r.Top + 13), 1.9, 1.9);
            }
        }
        else
        {
            // 感叹号
            dc.DrawRoundedRectangle(ink, null, new Rect(r.Left + 13, r.Top + 6, 4, 9), 2, 2);
            dc.DrawEllipse(ink, null, new Point(r.Left + 15, r.Top + 19), 2.1, 2.1);
        }
    }

    static void DrawMotion(DrawingContext dc, double t)
    {
        Pen p = new Pen(new SolidColorBrush(Color.FromArgb(0x77, 0x2E, 0x5C, 0x94)), 2.0);
        p.StartLineCap = PenLineCap.Round;
        double off = (t < 0.5) ? 3 : -3;
        dc.DrawLine(p, new Point(-48 + off, -12), new Point(-38 + off, -12));
        dc.DrawLine(p, new Point(-50 + off, -2), new Point(-41 + off, -2));
        dc.DrawLine(p, new Point(38 - off, -12), new Point(48 - off, -12));
        dc.DrawLine(p, new Point(41 - off, -2), new Point(50 - off, -2));
    }

    static void DrawSparkles(DrawingContext dc, double t)
    {
        // 四角星，两个交替闪烁
        for (int i = 0; i < 3; i++)
        {
            double phase = (t + i * 0.33) % 1.0;
            double s = 4 + Math.Sin(phase * Math.PI) * 4;
            double px = (i == 0) ? -42 : (i == 1 ? 42 : 34);
            double py = (i == 0) ? -26 : (i == 1 ? -30 : -20);
            byte a = (byte)(60 + Math.Sin(phase * Math.PI) * 195);
            DrawStar(dc, new Point(px, py), s, Color.FromArgb(a, CAccent.R, CAccent.G, CAccent.B));
        }
    }

    static void DrawStar(DrawingContext dc, Point c, double s, Color col)
    {
        StreamGeometry g = new StreamGeometry();
        using (StreamGeometryContext ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - s), true, true);
            ctx.LineTo(new Point(c.X + s * 0.26, c.Y - s * 0.26), true, false);
            ctx.LineTo(new Point(c.X + s, c.Y), true, false);
            ctx.LineTo(new Point(c.X + s * 0.26, c.Y + s * 0.26), true, false);
            ctx.LineTo(new Point(c.X, c.Y + s), true, false);
            ctx.LineTo(new Point(c.X - s * 0.26, c.Y + s * 0.26), true, false);
            ctx.LineTo(new Point(c.X - s, c.Y), true, false);
            ctx.LineTo(new Point(c.X - s * 0.26, c.Y - s * 0.26), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(new SolidColorBrush(col), null, g);
    }

    static void DrawZs(DrawingContext dc, int f, int count)
    {
        Typeface tf = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        for (int i = 0; i < count; i++)
        {
            double phase = (double)((f + i) % 4) / 4.0;
            double size = 11 + i * 4;
            byte a = (byte)(255 - phase * 140);
            FormattedText ft = new FormattedText("z", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tf, size,
                new SolidColorBrush(Color.FromArgb(a, CInk.R, CInk.G, CInk.B)), 1.0);
            // 从头顶向右上方飘散
            dc.DrawText(ft, new Point(16 + i * 14, -26 - i * 13 - phase * 5));
        }
    }

    static void DrawArc(DrawingContext dc, Point a, Point b, Point c, double thick, Brush br)
    {
        StreamGeometry g = new StreamGeometry();
        using (StreamGeometryContext ctx = g.Open())
        {
            ctx.BeginFigure(a, false, false);
            ctx.QuadraticBezierTo(b, c, true, false);
        }
        g.Freeze();
        Pen p = new Pen(br, thick);
        p.StartLineCap = PenLineCap.Round;
        p.EndLineCap = PenLineCap.Round;
        dc.DrawGeometry(null, p, g);
    }

    static Geometry EllipseGeo(Point c, double rx, double ry)
    {
        EllipseGeometry g = new EllipseGeometry(c, rx, ry);
        g.Freeze();
        return g;
    }
}
