// Cutout.cs —— 把人物从背景里抠出来（桌宠素材流水线第一步）
//
// 用法：
//   Cutout.exe <源图> <输出.png> [背景容差] [修正文件]
//
// 输出（都写到 <输出.png> 旁边）：
//   <输出.png>         透明 RGBA 的人物抠图：裁到 bbox、保持原始分辨率（不缩放）
//   <输出.check.png>   合成到洋红底上的检查图 —— 有没有抠干净一眼可见
//   <输出.mask.png>    原图 + 红色半透明标出"被判成背景抹掉"的像素 —— 有没有抠漏一眼可见
//   <输出.alpha.png>   只看 alpha 的灰度图
//   <输出.txt>         数值诊断（bbox、背景参考色、边框吻合度、被丢掉的连通块、残留自检）
//
// 算法：**背景色键控** —— 把"就是背景色"的像素全删掉，完事。
//
// 素材约定：人物画在**一块纯色背景**上，而且这块颜色和角色差得远。
// 满足这一条，抠图就是一行的事。
//
// 为什么是这么一个简单的算法（绕了一圈才到的，值得记）：
//   上一版素材的背景是奶油色、和人物皮肤只差 5（脸 (253,240,225) vs 背景 (253,246,230)）。
//   颜色判据怎么调都是错的 —— 阈值定宽了切人物、定窄了留背景。那时只能改用几何判据：
//   把深色描边膨胀成"堤坝"封住断口，从四边漫水，堤坝外面才算背景。它确实能把人物抠出来，
//   但代价是边界切在描边外沿，会把角色**自带的白色贴纸边一起切掉**；而被轮廓围死的背景块
//   （头发卷中间的洞）漫水进不去，还得再补一刀颜色判据去认它 —— 那一刀很脆：
//   实测同一块残留在 8 张图里的颜色吻合度能从 15% 跳到 79%，阈值怎么定都会漏或误伤。
//   2026-09-12 把素材背景重做成纯青色之后，上面所有麻烦一起消失了。
//   **结论：换素材时挑一块和角色明显不同的纯色背景，比事后补算法划算得多。**
//
// 键控顺带白拿的两件事：
//   · 角色自带的白色描边/贴纸边**完整保留**（它是画稿的一部分，不是背景）；
//   · 被轮廓围死的背景色也一视同仁地删掉 —— 逐像素判定，压根不问连通性。
//
// 自动流程不可能 100% 干净，所以留了修正文件的入口（见 ApplyFixes），
// 具体留哪几个补丁点由人看着检查图决定，并且记录在文件里、可复现。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

class Cutout
{
    // ---------- 可调参数 ----------
    static int BgFrame = 20;         // 取背景参考色用的边框宽度 —— 图像最外这一圈铁定是背景
    static int KeyTol = 30;          // 和背景参考色的最大通道差 ≤ 它 → 这个像素"就是背景色"
                                     // 实测：背景是纯青色、边框内吻合度 100%；而人物里最接近
                                     // 背景色的是眼睛的青蓝高光，差 45 —— 取 30 正好卡在中间。
                                     // 这个值**不能乱放**：放太宽（>45）就会把眼睛高光当成背景挖掉。
    static int ChromaMinUniform = 80; // 边框内 ≥ 它% 的像素接近背景参考色，才算"背景是纯色"。
                                      // 低于它 ChromaMatte 只**警告**、不换算法 ——
                                      // 这个工具就一条路，素材得配纯色背景。
    static int Tol = 30;           // 修正文件里 RegionGrow 的默认容差
    static int ErodePx = 1;        // 收边：往人物里收 1px，修掉抗锯齿在边缘留下的混色
    static int AlphaThresh = 128;  // 源图自带 alpha 时，认为"这是人物"的阈值

    // ---------- 数据 ----------
    static int W, H;
    static byte[] Px;              // Bgra32，直通（非预乘）

    static void Main(string[] argv)
    {
        if (argv.Length < 2)
        {
            Console.WriteLine("用法: Cutout.exe <源图> <输出.png> [背景容差] [修正文件]");
            Console.WriteLine("  背景容差 默认 30：和背景参考色的最大通道差 ≤ 它就算背景色");
            Console.WriteLine("  素材要画在**一块纯色背景**上、且这个颜色和角色差得远 —— 见文件头注释");
            return;
        }
        // 参数解析也要放进 try 里：以前它在外边，写错一个参数（比如手滑打成字母）
        // 会抛未处理的 FormatException，弹一个 .NET 的崩溃框出来，而不是这里统一的"失败: …"。
        try
        {
            string srcPath = argv[0];
            string outPath = argv[1];
            if (argv.Length >= 3 && argv[2].Length > 0)
                KeyTol = ParseInt(argv[2], 1, 255, "背景容差");

            string fixPath = (argv.Length >= 4 && argv[3].Length > 0) ? argv[3] : null;

            Run(srcPath, outPath, fixPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine("失败: " + ex.Message);
            Environment.ExitCode = 1;
        }
    }

    // 命令行参数写错时要明确说"哪个参数、允许什么范围"，
    // 而不是抛一个 FormatException 让人自己去猜哪儿写错了。
    // 顺带把范围卡住：背景容差给 0 会得到一个"一个像素都删不掉"的空操作，而且不报错。
    static int ParseInt(string s, int min, int max, string what)
    {
        int v;
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            throw new ArgumentException(what + " 得是个数字，收到的是 \"" + s + "\"（允许 " + min + "~" + max + "）");
        if (v < min || v > max)
            throw new ArgumentException(what + " 应该在 " + min + "~" + max + " 之间，收到 " + v);
        return v;
    }

    static void Run(string srcPath, string outPath, string fixPath)
    {
        LoadPixels(srcPath);
        Console.WriteLine("源图      : " + srcPath + "  " + W + "x" + H);
        Console.WriteLine("背景容差  : ±" + KeyTol + "（和背景参考色的最大通道差）");

        StringBuilder diag = new StringBuilder();
        diag.AppendLine("源图       : " + Path.GetFullPath(srcPath));
        diag.AppendLine("尺寸       : " + W + " x " + H);

        bool[] mask;   // true = 人物
        int alphaPixels = CountPartialAlpha();
        if (alphaPixels > W * H / 500)
        {
            // 源图自带透明通道：直接用，不抠
            Console.WriteLine("源图自带 alpha（" + alphaPixels + " 个半透明像素），直接走 alpha 路径");
            diag.AppendLine("路径       : 源图自带 alpha，未做抠图");
            mask = MaskFromAlpha();
        }
        else
        {
            diag.AppendLine("路径       : 无 alpha，按背景色键控抠图（容差 ±" + KeyTol + "）");
            mask = ChromaMatte(diag);
        }

        if (fixPath != null)
        {
            int n = ApplyFixes(fixPath, mask, diag);
            Console.WriteLine("修正文件   : " + fixPath + "  应用了 " + n + " 条");
        }

        // 收边 + 在 mask 上羽化
        mask = ErodeMask(mask, ErodePx);
        byte[] alpha = FeatherMask(mask, 1);

        WriteCutout(outPath, alpha);
        WriteDiagnostics(outPath, srcPath, mask, alpha, diag);

        int cnt = 0;
        for (int i = 0; i < alpha.Length; i++) if (alpha[i] > 0) cnt++;
        Console.WriteLine("人物像素   : " + cnt + "  (" + (100.0 * cnt / (W * H)).ToString("0.0", CultureInfo.InvariantCulture) + "%)");
        Console.WriteLine("bbox       : " + BboxText(alpha));
        Console.WriteLine("输出       : " + Path.GetFullPath(outPath));
        Console.WriteLine("检查图     : " + Path.GetFullPath(outPath) + ".check.png  (洋红底，看有没有残留背景)");
        Console.WriteLine("             " + Path.GetFullPath(outPath) + ".mask.png   (看有没有抠漏人物)");
    }

    // ================= 读图 =================
    static void LoadPixels(string path)
    {
        // 不信扩展名：让解码器自己认格式（这个项目里就有一张 .png 其实是 JPEG）
        BitmapDecoder dec = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource src = dec.Frames[0];

        FormatConvertedBitmap conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        W = conv.PixelWidth;
        H = conv.PixelHeight;
        int stride = W * 4;
        Px = new byte[stride * H];
        conv.CopyPixels(Px, stride, 0);
    }

    static int CountPartialAlpha()
    {
        int n = 0;
        for (int i = 3; i < Px.Length; i += 4)
        {
            if (Px[i] < 250) n++;
        }
        return n;
    }

    static bool[] MaskFromAlpha()
    {
        bool[] m = new bool[W * H];
        for (int i = 0; i < m.Length; i++)
        {
            m[i] = Px[i * 4 + 3] >= AlphaThresh;
        }
        return m;
    }

    static int Idx(int x, int y) { return y * W + x; }

    static int MaxDiff(int a, int b)
    {
        int d1 = Math.Abs(Px[a * 4] - Px[b * 4]);
        int d2 = Math.Abs(Px[a * 4 + 1] - Px[b * 4 + 1]);
        int d3 = Math.Abs(Px[a * 4 + 2] - Px[b * 4 + 2]);
        int m = d1;
        if (d2 > m) m = d2;
        if (d3 > m) m = d3;
        return m;
    }

    // ================= 抠图：背景色键控 =================
    // 素材约定：人物画在**一块纯色背景**上（本项目的素材是青色），而且这块颜色和角色差得远。
    // 于是要做的事只有一句：**把"就是背景色"的像素全删掉**。
    //
    // 为什么敢这么简单（这是绕了一圈才到的）：
    //   上一版素材的背景是奶油色、和人物皮肤只差 5（脸 (253,240,225) vs 背景 (253,246,230)），
    //   颜色判据怎么调都是错的 —— 阈值定宽了切人物、定窄了留背景。那时只能改用几何判据
    //   （深色描边膨胀成堤坝 → 从四边漫水），代价是边界切在描边外沿、把角色**自带的白色
    //   贴纸边一起切掉**，而且被轮廓围死的背景块还得再想办法补一刀。
    //   2026-09-12 把素材重做成纯青背景之后，上面那些全都不需要了。
    //   **换素材时挑一块和角色明显不同的纯色背景，比事后补算法划算得多。**
    //
    // 顺带白拿的两件事：
    //   · 角色自带的白色描边/贴纸边**完整保留** —— 它是画稿的一部分，不是背景；
    //   · 被轮廓围死的背景色（头发卷中间的洞）也一视同仁地被删掉，
    //     不需要"再抠一次"，因为它压根不问连通性，只问颜色。
    static bool[] ChromaMatte(StringBuilder diag)
    {
        // 1) 背景参考色 = 图像最外 BgFrame 宽那一圈的均色 —— 那里铁定是背景。
        //    **不要**改用"离某个像素最近的那块背景"：角色自带的白色描边也是"非背景色、
        //    连着边界"的，会被算成背景，于是青色残留的"最近背景"变成了白色，判据失效。
        long fR = 0, fG = 0, fB = 0;
        int fn = 0;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                if (x >= BgFrame && y >= BgFrame && x < W - BgFrame && y < H - BgFrame) continue;
                int i = y * W + x;
                fR += Px[i * 4 + 2]; fG += Px[i * 4 + 1]; fB += Px[i * 4];
                fn++;
            }
        }
        int bgR = (int)(fR / fn), bgG = (int)(fG / fn), bgB = (int)(fB / fn);

        // 2) 边框里有多少像素真的接近这个色 —— "这张图适不适合这么抠"的体检指标。
        //    背景不是纯色时它会掉下来。那时输出会很糟，所以**明确警告**，而不是闷头给一张烂图。
        bool[] isKey = new bool[W * H];
        for (int i = 0; i < isKey.Length; i++) isKey[i] = ChanDiff(i, bgR, bgG, bgB) <= KeyTol;
        int frameHit = 0;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                if (x >= BgFrame && y >= BgFrame && x < W - BgFrame && y < H - BgFrame) continue;
                if (isKey[y * W + x]) frameHit++;
            }
        }
        double uniform = 100.0 * frameHit / fn;
        diag.AppendLine("背景参考色 : (" + bgR + "," + bgG + "," + bgB + ")  边框内吻合 " +
                        uniform.ToString("0.0", CultureInfo.InvariantCulture) + "%（容差 ±" + KeyTol + "）");
        if (uniform < ChromaMinUniform)
            Console.WriteLine("警告: 背景看起来**不是一块纯色**（边框内只有 " +
                              uniform.ToString("0.0", CultureInfo.InvariantCulture) +
                              "% 的像素接近背景色）。键控是给纯色背景设计的，这张的结果可能很差。");

        // 3) 删掉所有背景色像素 —— 剩下的就是人物，**连同它自带的白色描边**
        bool[] fg = new bool[W * H];
        int bgCount = 0;
        for (int i = 0; i < fg.Length; i++)
        {
            fg[i] = !isKey[i];
            if (isKey[i]) bgCount++;
        }
        diag.AppendLine("填充结果   : 背景 " + bgCount + " px (" +
                        (100.0 * bgCount / (W * H)).ToString("0.0", CultureInfo.InvariantCulture) + "%)");

        // 4) 只留最大的一块 —— 顺手丢掉角落里那些"不是背景色、但也不是人物"的独立东西。
        //    实测素材右下角有「豆包AI生成」的水印文字，就是这么没的。
        fg = KeepLargestComponent(fg, diag);

        // 自检：还剩多少背景色像素。逐像素删的，这个值必然接近 0；留着是万一哪天
        // 又改成"按块判定"，能立刻看出来。
        int left = 0;
        for (int i = 0; i < fg.Length; i++) if (fg[i] && isKey[i]) left++;
        diag.AppendLine("残留自检   : 人物里还剩 " + left + " px 背景色（理想 0）");
        return fg;
    }

    // 某个像素和给定 RGB 的最大通道差
    static int ChanDiff(int i, int r, int g, int b)
    {
        int d1 = Math.Abs(Px[i * 4 + 2] - r), d2 = Math.Abs(Px[i * 4 + 1] - g), d3 = Math.Abs(Px[i * 4] - b);
        int m = d1;
        if (d2 > m) m = d2;
        if (d3 > m) m = d3;
        return m;
    }

    // ================= 连通块 =================
    // 返回每个像素的连通块编号（-1 = 不属于任何块），块面积和 bbox 走 outArea/outBox。
    // 以前签名里还有 outLabel/outSizes 两个参数，都是"输出参数化"的半成品：
    // outLabel 进函数就被重新分配覆盖、outSizes 从头到尾没被用过。删掉了。
    static int[] Label(bool[] mask, List<int> outArea, List<int[]> outBox)
    {
        int[] outLabel = new int[mask.Length];
        for (int i = 0; i < outLabel.Length; i++) outLabel[i] = -1;
        int[] stack = new int[mask.Length];
        int label = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || outLabel[i] >= 0) continue;
            int sp = 0;
            stack[sp++] = i;
            outLabel[i] = label;
            int area = 0;
            int minX = W, minY = H, maxX = -1, maxY = -1;
            while (sp > 0)
            {
                int p = stack[--sp];
                int x = p % W;
                int y = p / W;
                area++;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
                for (int k = 0; k < 8; k++)
                {
                    int nx = x + NX8[k];
                    int ny = y + NY8[k];
                    if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                    int np = ny * W + nx;
                    if (mask[np] && outLabel[np] < 0)
                    {
                        outLabel[np] = label;
                        stack[sp++] = np;
                    }
                }
            }
            outArea.Add(area);
            outBox.Add(new int[] { minX, minY, maxX, maxY });
            label++;
        }
        return outLabel;
    }

    static readonly int[] NX8 = new int[] { 1, -1, 0, 0, 1, 1, -1, -1 };
    static readonly int[] NY8 = new int[] { 0, 0, 1, -1, 1, -1, 1, -1 };

    static bool[] KeepLargestComponent(bool[] mask, StringBuilder diag)
    {
        List<int> area = new List<int>();
        List<int[]> box = new List<int[]>();
        int[] label = Label(mask, area, box);

        int best = -1, bestArea = 0;
        for (int i = 0; i < area.Count; i++)
        {
            if (area[i] > bestArea)
            {
                bestArea = area[i];
                best = i;
            }
        }
        bool[] kept = new bool[mask.Length];
        if (best < 0) return kept;
        for (int i = 0; i < label.Length; i++) kept[i] = (label[i] == best);

        diag.AppendLine("连通块     : 共 " + area.Count + " 块，保留最大的一块 " + bestArea + " px，丢弃 " + (area.Count - 1) + " 块");
        // 把被丢掉的、比较大的块报出来 —— 万一把人物的一部分也丢了，能在这里看出来
        List<int> idx = new List<int>();
        for (int i = 0; i < area.Count; i++) if (i != best) idx.Add(i);
        idx.Sort(delegate(int a, int b) { return area[b].CompareTo(area[a]); });
        for (int n = 0; n < idx.Count && n < 8; n++)
        {
            int i = idx[n];
            if (area[i] < 40) break;
            diag.AppendLine("  丢弃   : " + area[i] + " px  bbox=(" + box[i][0] + "," + box[i][1] + ")-(" +
                            box[i][2] + "," + box[i][3] + ")");
        }
        return kept;
    }

    // ================= 修正文件 =================
    // 自动流程的兜底。格式（每行一条，# 开头是注释）：
    //   erase <x> <y> [容差]   从这个点开始，把颜色相近的连通区域抹成背景
    //   keep  <x> <y> [容差]   从这个点开始，把颜色相近的连通区域恢复成人物
    static int ApplyFixes(string path, bool[] mask, StringBuilder diag)
    {
        // 用户明确指了修正文件却找不到 —— 必须说出来。
        // 静默跳过（以前是直接 return 0，然后照常输出一张"干净"的抠图）会让人以为是算法没调好，
        // 在参数上白折腾半天，而真正的原因只是路径写错了。
        if (!File.Exists(path))
        {
            Console.WriteLine("警告: 找不到修正文件 " + path + " —— 本次没有任何手工修正生效");
            diag.AppendLine("修正文件   : **找不到 " + path + "**，未应用任何修正（抠图是按自动结果输出的）");
            return 0;
        }

        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        int applied = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            // 单行写错不能让整次抠图白跑：以前这里的 int.Parse 一抛异常就整个中止，
            // 连抠图输出都没有。现在逐行兜住，把问题行号打出来，其余照常应用。
            try
            {
                string[] t = line.Split(new char[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 3)
                    throw new ArgumentException("格式应该是: erase/keep <x> <y> [容差]");

                string op = t[0].ToLowerInvariant();
                if (op != "erase" && op != "keep")
                    throw new ArgumentException("第一个词只能是 erase 或 keep，收到 \"" + t[0] + "\"");

                int x = int.Parse(t[1], CultureInfo.InvariantCulture);
                int y = int.Parse(t[2], CultureInfo.InvariantCulture);
                int tol = (t.Length >= 4) ? int.Parse(t[3], CultureInfo.InvariantCulture) : Tol;

                if (x < 0 || y < 0 || x >= W || y >= H)
                    throw new ArgumentException("坐标 (" + x + "," + y + ") 超出图片范围 " + W + "x" + H);

                int n = RegionGrow(x, y, tol, mask, (op == "erase"));
                diag.AppendLine("修正       : " + op + " (" + x + "," + y + ") 容差 " + tol + " → " + n + " px");
                applied++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("警告: 修正文件第 " + (i + 1) + " 行没用上：" + ex.Message + "   （" + line + "）");
                diag.AppendLine("修正跳过   : 第 " + (i + 1) + " 行 \"" + line + "\" → " + ex.Message);
            }
        }
        return applied;
    }

    // 从种子点按颜色相近生长，把连通的区域设成 keep 或 erase
    static int RegionGrow(int sx, int sy, int tol, bool[] mask, bool erase)
    {
        int seed = Idx(sx, sy);
        bool[] hit = new bool[W * H];
        int[] stack = new int[W * H];
        int sp = 0;
        stack[sp++] = seed;
        hit[seed] = true;
        int n = 0;
        while (sp > 0)
        {
            int p = stack[--sp];
            if (mask[p] != erase) { mask[p] = erase; n++; }
            int x = p % W;
            int y = p / W;
            for (int k = 0; k < 8; k++)
            {
                int nx = x + NX8[k];
                int ny = y + NY8[k];
                if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                int np = ny * W + nx;
                if (hit[np]) continue;
                if (MaxDiff(np, seed) <= tol)
                {
                    hit[np] = true;
                    stack[sp++] = np;
                }
            }
        }
        return n;
    }

    // ================= 收边 / 羽化 =================
    static bool[] ErodeMask(bool[] mask, int r)
    {
        if (r <= 0) return mask;
        bool[] cur = mask;
        for (int i = 0; i < r; i++)
        {
            bool[] next = new bool[cur.Length];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    bool all = true;
                    for (int k = 0; k < 8 && all; k++)
                    {
                        int nx = x + NX8[k];
                        int ny = y + NY8[k];
                        if (nx < 0 || ny < 0 || nx >= W || ny >= H) { all = false; break; }
                        if (!cur[ny * W + nx]) all = false;
                    }
                    next[Idx(x, y)] = cur[Idx(x, y)] && all;
                }
            }
            cur = next;
        }
        return cur;
    }

    // 注意：羽化作用在 mask 上、而不是在有颜色的图上。
    // 这样半透明的边缘像素拿到的是**人物自己的**颜色（深色描边），
    // 而不是"人物颜色和背景色混出来的一圈浅边" —— 省掉了 despill。
    static byte[] FeatherMask(bool[] mask, int r)
    {
        byte[] a = new byte[W * H];
        for (int i = 0; i < a.Length; i++) a[i] = mask[i] ? (byte)255 : (byte)0;
        if (r <= 0) return a;

        byte[] blurred = new byte[a.Length];
        int sum, cnt;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                sum = 0;
                cnt = 0;
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int nx = x + dx;
                        int ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                        sum += a[ny * W + nx];
                        cnt++;
                    }
                }
                blurred[Idx(x, y)] = (byte)(sum / cnt);
            }
        }
        return blurred;
    }

    // ================= 输出 =================
    static void WriteCutout(string outPath, byte[] alpha)
    {
        // 裁到人物 bbox（+2px 余量）再输出。
        // 为什么必须裁：下游 SheetGen 是"按高度适配 + 水平居中"摆位的，
        // 留着原来的画布空白，人物会既偏小又偏心。（诊断图仍然按整图输出，
        // 因为要能和原图逐像素对照着找问题。）
        int minX = W, minY = H, maxX = -1, maxY = -1;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                if (alpha[Idx(x, y)] < 128) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0)
        {
            Console.WriteLine("警告: 抠出来是空的，不裁剪");
            byte[] all = new byte[W * H * 4];
            Common.SavePngBgra(outPath, W, H, all);
            return;
        }
        minX -= 2; minY -= 2; maxX += 2; maxY += 2;
        if (minX < 0) minX = 0;
        if (minY < 0) minY = 0;
        if (maxX > W - 1) maxX = W - 1;
        if (maxY > H - 1) maxY = H - 1;
        int cw = maxX - minX + 1, ch = maxY - minY + 1;

        byte[] outPx = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++)
        {
            for (int x = 0; x < cw; x++)
            {
                int s = Idx(minX + x, minY + y);
                int d = y * cw + x;
                outPx[d * 4] = Px[s * 4];
                outPx[d * 4 + 1] = Px[s * 4 + 1];
                outPx[d * 4 + 2] = Px[s * 4 + 2];
                outPx[d * 4 + 3] = alpha[s];
            }
        }
        Common.SavePngBgra(outPath, cw, ch, outPx);
        Console.WriteLine("裁到 bbox  : (" + minX + "," + minY + ") " + cw + "x" + ch);
    }

    static void WriteDiagnostics(string outPath, string srcPath, bool[] mask, byte[] alpha, StringBuilder diag)
    {
        // 洋红底合成：残留的背景一眼可见
        byte[] check = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            int a = alpha[i];
            int r = (Px[i * 4 + 2] * a + 255 * (255 - a)) / 255;
            int g = (Px[i * 4 + 1] * a + 0 * (255 - a)) / 255;
            int b = (Px[i * 4] * a + 255 * (255 - a)) / 255;
            check[i * 4] = (byte)b;
            check[i * 4 + 1] = (byte)g;
            check[i * 4 + 2] = (byte)r;
            check[i * 4 + 3] = 255;
        }
        Common.SavePngBgra(outPath + ".check.png", W, H, check);

        // 原图上把"判成背景"的像素涂红：抠漏人物（人物被涂红）一眼可见
        byte[] mk = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            int r = Px[i * 4 + 2], g = Px[i * 4 + 1], b = Px[i * 4];
            if (alpha[i] < 128)
            {
                r = (r * 3 + 255 * 2) / 5;
                g = g * 3 / 5;
                b = b * 3 / 5;
            }
            mk[i * 4] = (byte)b;
            mk[i * 4 + 1] = (byte)g;
            mk[i * 4 + 2] = (byte)r;
            mk[i * 4 + 3] = 255;
        }
        Common.SavePngBgra(outPath + ".mask.png", W, H, mk);

        byte[] al = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            al[i * 4] = alpha[i];
            al[i * 4 + 1] = alpha[i];
            al[i * 4 + 2] = alpha[i];
            al[i * 4 + 3] = 255;
        }
        Common.SavePngBgra(outPath + ".alpha.png", W, H, al);

        diag.AppendLine("bbox       : " + BboxText(alpha));
        File.WriteAllText(outPath + ".txt", diag.ToString(), new UTF8Encoding(false));
    }

    static string BboxText(byte[] alpha)
    {
        int minX = W, minY = H, maxX = -1, maxY = -1;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                if (alpha[Idx(x, y)] < 128) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) return "(空)";
        return "(" + minX + "," + minY + ")-(" + maxX + "," + maxY + ")  " + (maxX - minX + 1) + "x" + (maxY - minY + 1);
    }

}
