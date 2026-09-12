// Cutout.cs —— 把人物从背景里抠出来（桌宠素材流水线第一步）
//
// 用法：
//   Cutout.exe <源图> <输出.png> [暗色阈值] [修正文件] [堤坝半径]
//
// 输出（都写到 <输出.png> 旁边）：
//   <输出.png>         透明 RGBA 的人物抠图：裁到 bbox、保持原始分辨率（不缩放）
//   <输出.check.png>   合成到洋红底上的检查图 —— 有没有抠干净一眼可见
//   <输出.mask.png>    原图 + 红色半透明标出"被判成背景抹掉"的像素 —— 有没有抠漏一眼可见
//   <输出.alpha.png>   只看 alpha 的灰度图
//   <输出.txt>         数值诊断（bbox、背景占比、样本色、被丢掉的连通块、封闭孔洞）
//
// 算法演进（第一版为什么失败，值得记住）：
//   第一版按"颜色像不像背景"判定，整片失败 —— 她的浅金发和皮肤跟浅色背景色差小到分不开
//   （脸 (253,240,225) vs 背景 (253,246,230)，只差 5），填充从描边的一个断口钻进去之后，
//   整个浅色躯干都被当成背景吞掉了。所以主判据必须是**几何**的：
//   0) 堤坝半径要跟着图的大小走 —— 描边的断口也是按比例放大的：
//      658px 宽的图 5px 够，1760x2304 的图 5px 会漏（浅色头发整片被吃掉，还不报错），
//      要 8px 以上。现在按图宽自动定（W/160，最小 5），可用第 5 个参数覆盖。
//   1) 深色描边 → 膨胀成"堤坝"：描边有几像素断口也封得住，而且完全不依赖颜色，
//      脸和背景同色也无所谓
//   2) 从四边在堤坝外漫水，再把背景膨胀回同样的半径 —— 边界正好落在描边外沿
//   3) 只保留最大连通块（星形描边残余、黄星星、网点、文字都是独立小块）
//   4) 封闭的缝单独判定：贴着轮廓 + 颜色就是背景色 → 抹掉；
//      脸/头发/白靴子这两条都不满足 → 保留
//   5) 收边 1px + 在 mask 上羽化（不在有颜色的图上羽化 —— 边缘就不会带一圈背景色）
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
    // 踩过的坑：第一版用"颜色像不像背景"来判定，结果整片失败 ——
    // 她的浅金发和皮肤跟浅色背景色差小到分不开（脸 (253,240,225) vs 背景 (253,246,230)，差 5），
    // 填充一旦从描边的某个断口钻进去，整个浅色躯干就被当成背景吞掉。
    // 所以主判据必须是**几何**的：深色描边 = 堤坝，从四边漫水，堤坝围不住的地方才是背景。
    // 颜色只用来区分"封闭的缝"（要抹掉）和"封闭的人物部件"（要保留）。
    static int Ldark = 150;        // 亮度低于它 = 描边/线条（堤坝原料）
    static int BarrierR = 5;       // 堤坝膨胀半径：描边有几像素的断口也靠它封住
    static int HoleMaxDist = 100;  // 粗筛保护"深处的人物部件"：实测真缝最远 58px，她的脸颊块 182px，取中间
    static int HoleColorTol = 12;  // 均色差 ≤ 它 = 这个封闭块的颜色就是背景色 → 是缝，抹掉
                                   // 实测分得很开：真缝 3~9，人物部件 14~96
    static int Tol = 30;           // 修正文件里 RegionGrow 的默认容差
    static int ErodePx = 1;        // 收边：往人物里收 1px，去掉 JPEG 振铃和浅色溢边
    static int AlphaThresh = 128;  // 源图自带 alpha 时，认为"这是人物"的阈值
    static bool BarrierRGiven = false;  // 命令行是否显式指定了堤坝半径

    // ---------- 数据 ----------
    static int W, H;
    static byte[] Px;              // Bgra32，直通（非预乘）
    static int[] DistToBg;         // 每个像素到最近背景像素的 4-邻域距离

    static void Main(string[] argv)
    {
        if (argv.Length < 2)
        {
            Console.WriteLine("用法: Cutout.exe <源图> <输出.png> [暗色阈值] [修正文件] [堤坝半径]");
            Console.WriteLine("  暗色阈值 默认 150；堤坝半径默认 5（像素越大、描边断口越宽的图要调大）");
            return;
        }
        // 参数解析也要放进 try 里：以前它在外边，写错一个参数（比如手滑打成字母）
        // 会抛未处理的 FormatException，弹一个 .NET 的崩溃框出来，而不是这里统一的"失败: …"。
        try
        {
            string srcPath = argv[0];
            string outPath = argv[1];
            if (argv.Length >= 3 && argv[2].Length > 0)
                Ldark = ParseInt(argv[2], 1, 255, "暗色阈值");

            string fixPath = (argv.Length >= 4 && argv[3].Length > 0) ? argv[3] : null;

            if (argv.Length >= 5 && argv[4].Length > 0)
            {
                BarrierR = ParseInt(argv[4], 1, 200, "堤坝半径");
                BarrierRGiven = true;
            }

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
    // 顺带把范围卡住：堤坝半径给 0 或负数会得到一个几乎全背景的空抠图，而且不报错。
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

        // 堤坝半径必须跟着图的大小走：描边的断口宽度也是按比例放大的。
        // 实测 658px 宽的图用 5px 够，1760x2304 的图 5px 会漏（浅色头发被整片吃掉，
        // 背景占比从 58% 涨到 68%），要 8px 以上才封得住 —— 所以按宽度自动定，
        // 命令行给了就用命令行的。
        if (!BarrierRGiven)
        {
            BarrierR = Math.Max(5, (int)Math.Round(W / 160.0));
        }
        Console.WriteLine("堤坝半径  : " + BarrierR + "px" + (BarrierRGiven ? "（命令行指定）" : "（按图宽自动）"));

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
            diag.AppendLine("路径       : 无 alpha，按深色描边做几何抠图（堤坝半径 " + BarrierR + "px）");
            mask = AutoMatte(diag);
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

    // ================= 自动抠图（几何判据） =================
    static bool[] AutoMatte(StringBuilder diag)
    {
        // 1) 深色像素 = 描边/线条，这是堤坝的原料
        bool[] dark = new bool[W * H];
        int darkCount = 0;
        for (int i = 0; i < dark.Length; i++)
        {
            int r = Px[i * 4 + 2], g = Px[i * 4 + 1], b = Px[i * 4];
            int lum = (r * 299 + g * 587 + b * 114) / 1000;
            if (lum < Ldark)
            {
                dark[i] = true;
                darkCount++;
            }
        }
        diag.AppendLine("暗色线条   : " + darkCount + " px（亮度 < " + Ldark + "）");

        // 2) 膨胀成堤坝。描边只有几像素的断口的话，靠这一步封住 ——
        //    这是整个算法里最关键的一步：不依赖颜色，所以脸和背景同色也无所谓。
        bool[] barrier = Morph(dark, true, BarrierR);
        int bc = 0;
        for (int i = 0; i < barrier.Length; i++) if (barrier[i]) bc++;
        diag.AppendLine("堤坝       : 膨胀 " + BarrierR + "px → " + bc + " px");

        // 3) 从四边在"非堤坝"上漫水：谁在堤坝外面谁就是背景
        bool[] bgOuter = FloodFromBorder(barrier);

        // 4) 把背景膨胀回 BarrierR，抵消第 2 步的膨胀，让边界回到描边的外沿。
        //    副作用正好是我想要的：小的深色斑点/文字笔画（直径 ≤ 2R）会被背景吃掉。
        bool[] bg = Morph(bgOuter, true, BarrierR);

        bool[] fg = new bool[W * H];
        int bgCount = 0;
        for (int i = 0; i < fg.Length; i++)
        {
            fg[i] = !bg[i];
            if (bg[i]) bgCount++;
        }
        diag.AppendLine("填充结果   : 背景 " + bgCount + " px (" +
                        (100.0 * bgCount / (W * H)).ToString("0.0", CultureInfo.InvariantCulture) + "%)");

        // 轻量闭运算：JPEG 噪点可能把细头发丝断开，断了人物就会碎成几块
        fg = Morph(fg, true, 1);
        fg = Morph(fg, false, 1);

        fg = KeepLargestComponent(fg, diag);
        // 注意这里传的是 bgOuter（没膨胀的真背景），不是 bg：
        // bg 里含膨胀出来的那一圈描边暗像素，拿它当参考色会把色差算歪
        // （实测参考色变成 (204,204,205) 而不是纸张色 (250,246,240)）。
        fg = RemoveEnclosedBackground(fg, bgOuter, diag);
        return fg;
    }

    static bool[] FloodFromBorder(bool[] barrier)
    {
        bool[] bg = new bool[W * H];
        int[] stack = new int[W * H];
        int sp = 0;
        for (int x = 0; x < W; x++)
        {
            Seed(x, 0, barrier, bg, stack, ref sp);
            Seed(x, H - 1, barrier, bg, stack, ref sp);
        }
        for (int y = 0; y < H; y++)
        {
            Seed(0, y, barrier, bg, stack, ref sp);
            Seed(W - 1, y, barrier, bg, stack, ref sp);
        }
        while (sp > 0)
        {
            int p = stack[--sp];
            int x = p % W;
            int y = p / W;
            for (int k = 0; k < 4; k++)     // 4-邻域：斜向连通会从描边的对角缝里漏进去
            {
                int nx = x + NX8[k];
                int ny = y + NY8[k];
                if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                int np = ny * W + nx;
                if (bg[np] || barrier[np]) continue;
                bg[np] = true;
                stack[sp++] = np;
            }
        }
        return bg;
    }

    static void Seed(int x, int y, bool[] barrier, bool[] bg, int[] stack, ref int sp)
    {
        int p = y * W + x;
        if (bg[p] || barrier[p]) return;
        bg[p] = true;
        stack[sp++] = p;
    }

    // ================= 形态学 =================
    // 用 3x3 结构元迭代 r 次 —— 单次扫"距离 r 的邻居"会在大 r 时留下空隙，
    // 边界就不准了（边界直接决定抠图的边缘，不能糊）。
    static bool[] Morph(bool[] src, bool dilate, int r)
    {
        bool[] cur = src;
        for (int i = 0; i < r; i++)
        {
            bool[] next = new bool[cur.Length];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    bool hit = cur[Idx(x, y)];
                    bool flip = false;
                    for (int k = 0; k < 8 && !flip; k++)
                    {
                        int nx = x + NX8[k];
                        int ny = y + NY8[k];
                        if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                        if (cur[ny * W + nx] != hit) flip = true;
                    }
                    next[Idx(x, y)] = dilate ? (hit || flip) : (hit && !flip);
                }
            }
            cur = next;
        }
        return cur;
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

    // ================= 封闭的缝 =================
    // 胳膊、腿、头发卷之间的缝：堤坝把它们围住了，所以填充够不到，会留在人物里。
    // 但人物的脸/头发/白靴子同样"被围住"，怎么区分？
    //   1) 缝就贴着轮廓：整块离真正的背景都很近（隔着一条描边而已）
    //   2) 缝的颜色就是背景色（同样的条纹）—— 而她的皮肤虽然和背景同色，
    //      却离轮廓很远；头皮/头发虽然贴着轮廓，颜色却和背景差得远
    // 两个条件同时满足才抹掉。先看数据再定阈值（诊断里把每块都列出来）。
    static bool[] RemoveEnclosedBackground(bool[] fg, bool[] bg, StringBuilder diag)
    {
        // 到最近背景像素的距离，以及那个背景像素的颜色
        DistToBg = new int[W * H];
        int[] nearest = new int[W * H];
        int[] queue = new int[W * H];
        int head = 0, tail = 0;
        for (int i = 0; i < DistToBg.Length; i++)
        {
            DistToBg[i] = int.MaxValue;
            if (bg[i])
            {
                DistToBg[i] = 0;
                nearest[i] = (Px[i * 4 + 2] << 16) | (Px[i * 4 + 1] << 8) | Px[i * 4];
                queue[tail++] = i;
            }
        }
        while (head < tail)
        {
            int p = queue[head++];
            int x = p % W;
            int y = p / W;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + NX8[k];
                int ny = y + NY8[k];
                if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                int np = ny * W + nx;
                if (DistToBg[np] > DistToBg[p] + 1)
                {
                    DistToBg[np] = DistToBg[p] + 1;
                    nearest[np] = nearest[p];
                    queue[tail++] = np;
                }
            }
        }

        // 候选 = 人物里"非深色"的连通块（深色的是描边和线条，不是缝）
        bool[] cand = new bool[W * H];
        for (int i = 0; i < cand.Length; i++)
        {
            if (!fg[i]) continue;
            int r = Px[i * 4 + 2], g = Px[i * 4 + 1], b = Px[i * 4];
            int lum = (r * 299 + g * 587 + b * 114) / 1000;
            cand[i] = lum >= Ldark;
        }

        int[] label;
        List<int> area = new List<int>();
        List<int[]> box = new List<int[]>();
        label = Label(cand, area, box);

        int removed = 0, removedPx = 0, keptRegions = 0;
        for (int i = 0; i < area.Count; i++)
        {
            if (area[i] < 12) continue;      // 太小的噪声不管
            int maxDist = 0;
            long sumR = 0, sumG = 0, sumB = 0;
            long bgR = 0, bgG = 0, bgB = 0;
            int cnt = 0;
            for (int p = 0; p < label.Length; p++)
            {
                if (label[p] != i) continue;
                if (DistToBg[p] > maxDist) maxDist = DistToBg[p];
                sumR += Px[p * 4 + 2];
                sumG += Px[p * 4 + 1];
                sumB += Px[p * 4];
                bgR += (nearest[p] >> 16) & 0xFF;
                bgG += (nearest[p] >> 8) & 0xFF;
                bgB += nearest[p] & 0xFF;
                cnt++;
            }
            // 块自己均色 vs "最近的背景"的均色 —— 都用均值，避免被条纹自身的明暗差异干扰
            int meanR = (int)(sumR / cnt), meanG = (int)(sumG / cnt), meanB = (int)(sumB / cnt);
            int nbR = (int)(bgR / cnt), nbG = (int)(bgG / cnt), nbB = (int)(bgB / cnt);
            int d1 = Math.Abs(meanR - nbR), d2 = Math.Abs(meanG - nbG), d3 = Math.Abs(meanB - nbB);
            int cdiff = d1;
            if (d2 > cdiff) cdiff = d2;
            if (d3 > cdiff) cdiff = d3;

            bool isGap = (maxDist <= HoleMaxDist) && (cdiff <= HoleColorTol);
            if (isGap)
            {
                for (int p = 0; p < label.Length; p++)
                {
                    if (label[p] == i) fg[p] = false;
                }
                removed++;
                removedPx += area[i];
                if (area[i] >= 100)
                {
                    diag.AppendLine("  抹掉缝  : " + area[i] + " px bbox=(" + box[i][0] + "," + box[i][1] + ")-(" +
                                    box[i][2] + "," + box[i][3] + ") 最远距背景=" + maxDist +
                                    " 均色=(" + meanR + "," + meanG + "," + meanB + ") 色差=" + cdiff);
                }
            }
            else
            {
                keptRegions++;
                if (area[i] >= 100)
                {
                    diag.AppendLine("  保留块: " + area[i] + " px bbox=(" + box[i][0] + "," + box[i][1] + ")-(" +
                                    box[i][2] + "," + box[i][3] + ") 最远距背景=" + maxDist +
                                    " 均色=(" + meanR + "," + meanG + "," + meanB + ")" +
                                    " 近背景均色=(" + nbR + "," + nbG + "," + nbB + ") 色差=" + cdiff);
                }
            }
        }
        diag.AppendLine("封闭的缝   : 抹掉 " + removed + " 块(" + removedPx + " px)，保留 " + keptRegions +
                        " 块（判据：最远距背景 ≤ " + HoleMaxDist + "px 且 均色差 ≤ " + HoleColorTol + "）");
        return fg;
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
            WritePng(outPath, all);
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
        WritePngSize(outPath, cw, ch, outPx);
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
        WritePng(outPath + ".check.png", check);

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
        WritePng(outPath + ".mask.png", mk);

        byte[] al = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            al[i * 4] = alpha[i];
            al[i * 4 + 1] = alpha[i];
            al[i * 4 + 2] = alpha[i];
            al[i * 4 + 3] = 255;
        }
        WritePng(outPath + ".alpha.png", al);

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

    static void WritePng(string path, byte[] bgra)
    {
        WritePngSize(path, W, H, bgra);
    }

    static void WritePngSize(string path, int w, int h, byte[] bgra)
    {
        BitmapSource bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        PngBitmapEncoder enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bs));
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir.Length > 0 && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        using (FileStream fs = File.Create(path))
        {
            enc.Save(fs);
        }
    }
}
