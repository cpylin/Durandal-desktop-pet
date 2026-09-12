// SheetGen.cs —— 从抠好的图生成桌宠用的 sprite sheet（素材流水线第二步）
//
// 用法：
//   SheetGen.exe <默认cutout.png> [状态=cutout.png ...] [--awake=状态名] [--out=路径] [--manifest=路径]
//
//   实际在用的那条（每个状态一张图，抠图按**姿势**命名，不是按状态命名）：
//     SheetGen.exe assets\cut\cheer.png ^
//         idle=assets\cut\stand.png   think=assets\cut\think.png ^
//         work=assets\cut\clasp.png   alert=assets\cut\wave.png  --awake=alert
//   （状态名不写就沿用"默认"那张。`--awake=<状态名>` 是"awake 复用它某一行的动作"，
//     只在 awake **不在** States 表里时才有意义 —— 当前表里有 awake，所以这个参数是空转的，
//     留着是为了以后把 awake 从状态表里拿掉的情况。）
//
// 输出：sprite sheet（PNG-32 带 alpha）+ pet.json。
// **pet.json 由本工具自己写**，保证清单和 sheet 永远对得上
// （MakeSprites.exe 也一样自己写清单 —— 手改 frameWidth 却忘了改另一个，是必然踩的坑）。
//
// 设计约束（很重要）：动作 = 那一张图的**平移/旋转/缩放**合成，不是重画的帧。
// 所以幅度和速度是照着"一眼能读出来是哪个状态"调的，而且这些参数**与具体画稿无关** ——
// 换一张姿势图，动作参数不用动（下面各状态的注释说的是"要什么感觉"，不是"这张图什么样"）：
//   idle 收敛；think 只歪头；alert/done 才允许大跳。
// 跳动统一用 -Abs(Sin(...))：身体只往上走、落下时回到原位，比 Sin 更像"跳"。
// 非循环的动作用 t = f/(n-1)（不是 f/n），这样最后一帧正好回到静止姿势 ——
// 程序会停在这一帧上，停在半空中会很怪。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

class SheetGen
{
    // ---------- 画布 ----------
    const int FrameW = 144;        // 显示尺寸（逻辑像素），写进 pet.json 的 frameWidth
    const int FrameH = 192;
    // 素材像素密度，写进 pet.json 的 **sheetScale**（键名和这个常量名不一样，别搜错）；
    // Pet.cs 用 sheetScale 把像素尺寸换成屏幕逻辑尺寸。
    const int PixelScale = 2;      // sheet 里每帧实际是 288x384
    const int Cols = 8;            // 每行最多几帧（= 最多的那个状态的帧数）

    // 角色在帧里的摆放
    const double FitH = 0.88;      // 占帧高比例（留出跳起来的空间）
    const double FitW = 0.94;
    const double BottomMargin = 0.04;

    // ---------- 状态表 ----------
    // 行序 = 这个数组的顺序。加状态就在这里加一条（Pet.cs 认 manifest 里的名字，不用改代码）。
    // awake 也在这里占一行（原来它是"复用某个状态的帧"，现在有自己的姿势了）——
    // 它是非循环的：Pet.cs 会在 2.5 秒后把 awake 衰减回 idle（DecayMs）。
    static readonly string[] States = new string[] { "idle", "think", "work", "alert", "done", "awake", "sleep", "error" };
    static readonly int[] Frames = new int[] { 4, 4, 6, 6, 8, 6, 4, 6 };
    static readonly double[] Fps = new double[] { 6, 6, 12, 10, 12, 10, 3, 8 };
    static readonly bool[] Loop = new bool[] { true, true, true, true, false, false, true, false };

    // 配色（和占位素材一套）
    static readonly Color CInk = Color.FromRgb(0x33, 0x4A, 0x66);
    static readonly Color CLine = Color.FromRgb(0x2E, 0x5C, 0x94);
    static readonly Color CGold = Color.FromRgb(0xFF, 0xC8, 0x4A);

    // 每个状态各自的适配比例（见 Run 里的说明：不能全局共用）
    static double FitOf(BitmapSource img)
    {
        return Math.Min(FrameH * FitH / img.PixelHeight, FrameW * FitW / img.PixelWidth);
    }

    // ---- 视觉大小微调 ----
    // 光把 bbox 高度统一是**不够**的：站姿的"头"只占 bbox 高的 ~23%，
    // 盘坐/蜷缩的姿势能占到 ~32% —— 于是同样 bbox 高度下，坐着的那几个**头明显更大**，
    // 看上去就是"这个状态怎么变大了"。（用户实际反馈：睡觉和出错偏大。）
    // 这里是手工微调值，靠肉眼看生成出来的 sheet 定的，改完要看图验收。
    //   小于 1 = 缩小；1 = 不动。只调"坐/蜷"这类姿态，站姿一律 1。
    static double ScaleAdjust(int row)
    {
        switch (States[row])
        {
            case "think": return 0.93;   // 盘腿坐
            case "sleep": return 0.88;   // 蜷在云上
            case "error": return 0.86;   // 抱膝缩成一团
            case "work":  return 1.05;   // 有书桌撑满了 bbox，人反而显小
        }
        return 1.0;
    }

    static void Main(string[] argv)
    {
        if (argv.Length < 1)
        {
            Console.WriteLine("用法: SheetGen.exe <默认cutout.png> [状态=cutout.png ...] [--out=路径] [--manifest=路径]");
            return;
        }
        try
        {
            Run(argv);
        }
        catch (Exception ex)
        {
            Console.WriteLine("失败: " + ex.Message);
            Console.WriteLine(ex.StackTrace);
            Environment.ExitCode = 1;
        }
    }

    static void Run(string[] argv)
    {
        string defaultPath = null;
        // 默认输出路径相对 **exe** 算，不相对当前目录。
        // README 里的用法是在 D:\ClaudePet 下跑 ./bin/SheetGen.exe，如果还写 "..\assets\pet.png"，
        // 会解析成 D:\assets\pet.png —— 另一个目录。用户会看到"已生成 D:\assets\pet.png"，
        // 而桌宠照旧用着老素材，还以为换素材失败了。
        string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        string projRoot = Path.GetFullPath(Path.Combine(exeDir, ".."));
        string outPath = Path.Combine(projRoot, "assets", "pet.png");
        string manifestPath = Path.Combine(projRoot, "assets", "pet.json");
        string awakeFrom = "done";      // awake（打招呼）复用哪个状态的帧，可用 --awake= 指定
        Dictionary<string, string> perState = new Dictionary<string, string>();

        for (int i = 0; i < argv.Length; i++)
        {
            string a = argv[i];
            if (a.StartsWith("--out=")) outPath = a.Substring(6);
            else if (a.StartsWith("--awake=")) awakeFrom = a.Substring(8);
            else if (a.StartsWith("--manifest=")) manifestPath = a.Substring(11);
            else if (a.IndexOf('=') > 0)
            {
                int eq = a.IndexOf('=');
                perState[a.Substring(0, eq)] = a.Substring(eq + 1);
            }
            else if (defaultPath == null) defaultPath = a;
        }
        if (defaultPath == null)
        {
            Console.WriteLine("缺少默认 cutout 路径");
            Environment.ExitCode = 1;
            return;
        }

        // 每个状态用哪张图：优先专用，其次默认
        BitmapSource[] images = new BitmapSource[States.Length];
        for (int s = 0; s < States.Length; s++)
        {
            string p = perState.ContainsKey(States[s]) ? perState[States[s]] : defaultPath;
            images[s] = Load(p);
            Console.WriteLine(States[s] + " ← " + Path.GetFileName(p) +
                              "  (" + images[s].PixelWidth + "x" + images[s].PixelHeight + ")");
        }

        // 每个状态各自算适配比例 —— **不要**改成一个全局共用的像素比。
        // 原因是素材可能来自分辨率差很多的不同图：这套里 cheer 是 658x860，
        // 另外三张是 1760x2304。共用一个"像素→逻辑像素"比例的话，
        // 低分辨率那张会显示得只有别人的 1/3 大（今天的真实事故）。
        // 按各自的图适配到最大才对；代价是不同姿势 bbox 高度略有差异（举手的姿势高一些），
        // 屏幕大小会有 3~5% 的出入 —— 比某个状态突然缩小一半好得多。
        for (int s = 0; s < States.Length; s++)
        {
            double f = FitOf(images[s]) * ScaleAdjust(s);
            double dw = images[s].PixelWidth * f;
            double dh = images[s].PixelHeight * f;
            // 越界会被每格的裁切悄悄切掉，所以这里显式检查（+12 是给 done 那个大跳留的头顶空间）
            bool ok = (dw <= FrameW - 2) && (dh + 12 <= FrameH);
            Console.WriteLine("  " + States[s].PadRight(6) + " 缩放 " +
                              f.ToString("0.0000", CultureInfo.InvariantCulture) +
                              "  画面里 " + Math.Round(dw) + "x" + Math.Round(dh) + " 逻辑px" +
                              (ok ? "" : "   ← 超框，会被裁掉！"));
        }

        int rows = States.Length;
        int w = Cols * FrameW * PixelScale;
        int h = rows * FrameH * PixelScale;

        DrawingVisual dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (DrawingContext dc = dv.RenderOpen())
        {
            for (int row = 0; row < rows; row++)
            {
                int n = Frames[row];
                for (int f = 0; f < n; f++)
                {
                    double cellX = f * FrameW * PixelScale;
                    double cellY = row * FrameH * PixelScale;
                    // 每帧单独裁切，免得动作/装饰溢出到相邻格子
                    dc.PushClip(new RectangleGeometry(new Rect(cellX, cellY, FrameW * PixelScale, FrameH * PixelScale)));
                    dc.PushTransform(new TranslateTransform(cellX, cellY));
                    dc.PushTransform(new ScaleTransform(PixelScale, PixelScale));   // 之后都按逻辑像素画
                    DrawFrame(dc, row, f, n, images[row]);
                    dc.Pop();
                    dc.Pop();
                    dc.Pop();
                }
            }
        }

        RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        PngBitmapEncoder enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        EnsureDir(outPath);
        using (FileStream fs = File.Create(outPath))
        {
            enc.Save(fs);
        }
        WriteManifest(manifestPath, awakeFrom);

        Console.WriteLine("已生成 " + Path.GetFullPath(outPath) + "  (" + w + "x" + h + ", " + Cols + " 列 x " +
                          rows + " 行, 单帧 " + (FrameW * PixelScale) + "x" + (FrameH * PixelScale) + " px)");
        Console.WriteLine("已写出 " + Path.GetFullPath(manifestPath));
    }

    static BitmapSource Load(string path)
    {
        BitmapImage b = new BitmapImage();
        b.BeginInit();
        b.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
        b.CacheOption = BitmapCacheOption.OnLoad;   // 立即读完，不锁文件
        b.EndInit();
        b.Freeze();
        return b;
    }

    // ---------- 一帧 ----------
    static void DrawFrame(DrawingContext dc, int row, int f, int n, BitmapSource img)
    {
        bool loop = Loop[row];
        double t = (n <= 1) ? 0 : (loop ? (double)f / n : (double)f / (n - 1));

        double bob = 0, dx = 0, tilt = 0, sx = 1, sy = 1;
        // 按**状态名**分支，不按行号 —— 行号和 States 数组的顺序绑在一起，
        // 中间插入一个状态会让后面所有状态串味（alert 演成 done 的动作）。
        // 名字认的话，正序倒序都不会错。（Frames/Fps/Loop 仍是按位置对齐的平行数组，
        // 加状态只能加在**末尾**。）
        switch (States[row])
        {
            case "idle":   // 待机要收敛 —— 轻微的呼吸浮动，不能像在蹦
                bob = Math.Sin(t * Math.PI * 2) * 1.6;
                sy = 1 + Math.Sin(t * Math.PI * 2) * 0.012;
                sx = 1 - Math.Sin(t * Math.PI * 2) * 0.008;
                break;
            case "think":  // 只轻轻歪头，靠「…」传达"在想"
                tilt = Math.Sin(t * Math.PI * 2) * 3.0;
                bob = -Math.Abs(Math.Sin(t * Math.PI * 2)) * 0.8;
                break;
            case "work":   // 快节奏的左右小幅抖动 —— "在忙"，比 think 明显但不跳
                dx = Math.Sin(t * Math.PI * 4) * 1.4;
                bob = Math.Sin(t * Math.PI * 4) * 2.2;
                break;
            case "alert":  // 弹跳 —— 意思是"在等你"，动作明显一点没关系
                bob = -Math.Abs(Math.Sin(t * Math.PI * 2)) * 6.0;
                sy = 1 + Math.Abs(Math.Sin(t * Math.PI * 2)) * 0.02;
                sx = 1 - Math.Abs(Math.Sin(t * Math.PI * 2)) * 0.014;
                break;
            case "done":   // 一次大跳，起落都回到静止位（非循环，会停最后一帧）
                bob = -Math.Abs(Math.Sin(t * Math.PI)) * 12.0;
                sy = 1 + Math.Abs(Math.Sin(t * Math.PI)) * 0.05;
                sx = 1 - Math.Abs(Math.Sin(t * Math.PI)) * 0.03;
                break;
            case "awake":  // 打招呼 —— 两次小跳（比 done 收敛、比 alert 轻快）
                bob = -Math.Abs(Math.Sin(t * Math.PI * 2)) * 4.0;
                sy = 1 + Math.Abs(Math.Sin(t * Math.PI * 2)) * 0.02;
                break;
            case "sleep":  // 慢呼吸 —— 整个人往下沉一点点（睡着了更放松），幅度必须小
                bob = 2.5 + Math.Sin(t * Math.PI * 2) * 0.8;
                sy = 1 + Math.Sin(t * Math.PI * 2) * 0.006;
                break;
            case "error":  // 小幅快速摇晃（像在发抖），起落都回到静止位（非循环）
                tilt = Math.Sin(t * Math.PI * 4) * 5.0;
                bob = 1.5 + Math.Sin(t * Math.PI * 4) * 0.6;
                break;
        }

        // 摆位：底边对齐（留 BottomMargin），水平居中。
        // 比例 = 该状态自己的适配比例 × 视觉微调（见 ScaleAdjust）。
        double fit = FitOf(img) * ScaleAdjust(row);
        double dw = img.PixelWidth * fit;
        double dh = img.PixelHeight * fit;
        double footY = FrameH - FrameH * BottomMargin;
        double x = (FrameW - dw) / 2;
        double y = footY - dh;
        Rect dest = new Rect(x, y, dw, dh);

        // 变换的支点放在"脚底中心"：跳和歪头都像从地面发力，而不是绕画面中心甩
        double px = FrameW / 2, py = footY;
        dc.PushTransform(new TranslateTransform(dx, bob));
        dc.PushTransform(new RotateTransform(tilt, px, py));
        dc.PushTransform(new ScaleTransform(sx, sy, px, py));
        dc.DrawImage(img, dest);
        dc.Pop();
        dc.Pop();
        dc.Pop();

        // 装饰画在人物变换之外（不跟着跳），但仍然在逻辑坐标里
        if (row == 1) DrawEllipsis(dc, f);
        if (row == 3) DrawBang(dc, t);
        if (row == 4) DrawSparkles(dc, t);
    }

    // ---------- 装饰 ----------
    static void DrawPlate(DrawingContext dc, Rect r)
    {
        SolidColorBrush fill = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        Pen pen = new Pen(new SolidColorBrush(CLine), 1.4);
        dc.DrawRoundedRectangle(fill, pen, r, 8, 8);
    }

    // think 的「…」：三个点轮流亮，像在转圈想事情
    static void DrawEllipsis(DrawingContext dc, int f)
    {
        Rect r = new Rect(FrameW - 46, 8, 40, 24);
        DrawPlate(dc, r);
        for (int i = 0; i < 3; i++)
        {
            byte a = (byte)((i <= f % 3) ? 0xFF : 0x55);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(a, CInk.R, CInk.G, CInk.B)), null,
                           new Point(r.Left + 10 + i * 10, r.Top + 12), 2.2, 2.2);
        }
    }

    // alert 的「!」：等你确认
    static void DrawBang(DrawingContext dc, double t)
    {
        Rect r = new Rect(FrameW - 40, 6, 30, 38);
        DrawPlate(dc, r);
        byte a = (byte)(0xFF - Math.Abs(Math.Sin(t * Math.PI * 2)) * 0x60);
        SolidColorBrush ink = new SolidColorBrush(Color.FromArgb(a, CInk.R, CInk.G, CInk.B));
        dc.DrawRoundedRectangle(ink, null, new Rect(r.Left + 12, r.Top + 8, 6, 15), 3, 3);
        dc.DrawEllipse(ink, null, new Point(r.Left + 15, r.Top + 29), 3.0, 3.0);
    }

    // done 的星星：三颗错开闪
    static void DrawSparkles(DrawingContext dc, double t)
    {
        double[] xs = new double[] { 20, FrameW - 22, 16 };
        double[] ys = new double[] { 30, 58, FrameH - 44 };
        for (int i = 0; i < 3; i++)
        {
            double phase = (t + i * 0.33) % 1.0;
            double s = 4 + Math.Sin(phase * Math.PI) * 4;
            byte a = (byte)(60 + Math.Sin(phase * Math.PI) * 195);
            DrawStar(dc, new Point(xs[i], ys[i]), s, Color.FromArgb(a, CGold.R, CGold.G, CGold.B));
        }
    }

    static void DrawStar(DrawingContext dc, Point c, double s, Color col)
    {
        StreamGeometry g = new StreamGeometry();
        using (StreamGeometryContext ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - s), true, true);
            ctx.LineTo(new Point(c.X + s * 0.28, c.Y - s * 0.28), true, false);
            ctx.LineTo(new Point(c.X + s, c.Y), true, false);
            ctx.LineTo(new Point(c.X + s * 0.28, c.Y + s * 0.28), true, false);
            ctx.LineTo(new Point(c.X, c.Y + s), true, false);
            ctx.LineTo(new Point(c.X - s * 0.28, c.Y + s * 0.28), true, false);
            ctx.LineTo(new Point(c.X - s, c.Y), true, false);
            ctx.LineTo(new Point(c.X - s * 0.28, c.Y - s * 0.28), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(new SolidColorBrush(col), null, g);
    }

    // ---------- pet.json ----------
    static void WriteManifest(string path, string awakeFrom)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"_说明\": \"素材清单。这份清单由 SheetGen.exe 自动生成，和 pet.png 一定对得上。\\n");
        sb.Append("           要换形象请改素材本身（见 README 的『换成你自己的形象』），不要手改这里的尺寸。\",\n");
        sb.Append("  \"_尺寸\": \"frameWidth/frameHeight 是 sheet 里**每个格子的像素尺寸**；\\n");
        sb.Append("           sheetScale 是素材像素密度 —— 屏幕上显示的大小 = frameWidth/sheetScale。\",\n");
        sb.Append("\n");
        sb.Append("  \"sheet\": \"pet.png\",\n");
        sb.Append("  \"frameWidth\": ").Append(FrameW * PixelScale).Append(",\n");
        sb.Append("  \"frameHeight\": ").Append(FrameH * PixelScale).Append(",\n");
        sb.Append("  \"sheetScale\": ").Append(PixelScale).Append(",\n");
        sb.Append("\n");
        // awake 是否已经在状态表里 —— 决定要不要补一条"复用某状态"的 awake。
        // 两边都写的话 JSON 里会有两个 "awake" 键，解析取最后一个，行为不可预期。
        bool awakeInStates = false;
        for (int i = 0; i < States.Length; i++) if (States[i] == "awake") awakeInStates = true;

        sb.Append("  \"animations\": {\n");
        for (int i = 0; i < States.Length; i++)
        {
            sb.Append("    \"").Append(States[i]).Append("\": { \"row\": ").Append(i)
              .Append(", \"frames\": ").Append(Frames[i])
              .Append(", \"fps\": ").Append(Fps[i].ToString("0.#", CultureInfo.InvariantCulture))
              .Append(", \"loop\": ").Append(Loop[i] ? "true " : "false")
              .Append(" }");
            if (i < States.Length - 1 || !awakeInStates) sb.Append(",");
            sb.Append("\n");
        }
        if (!awakeInStates)
        {
            // awake 没有自己的行：复用某个状态的帧，用 --awake=<状态名> 指定（默认 done）
            int awakeRow = 0;
            for (int i = 0; i < States.Length; i++) if (States[i] == awakeFrom) awakeRow = i;
            sb.Append("    \"awake\": { \"row\": ").Append(awakeRow)
              .Append(", \"frames\": ").Append(Frames[awakeRow])
              .Append(", \"fps\": 10, \"loop\": false }\n");
        }
        sb.Append("  }\n");
        sb.Append("}\n");
        EnsureDir(path);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    static void EnsureDir(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir.Length > 0 && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }
}
