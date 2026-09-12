// Zoom.cs —— 放大对比工具（调试用，不是桌宠的一部分）
//
// 用途：桌宠的窗口是分层窗口，截图截不到（README 坑 6）；而检查图整张 658x860
// 在我这边显示出来只有几百像素宽，边缘和细节根本看不清。所以需要"裁一小块放大 N 倍"。
//
// 用法：
//   Zoom.exe <图> <x> <y> <w> <h> <倍数> <输出.png>
//   Zoom.exe <图A> <图B> <x> <y> <w> <h> <倍数> <输出.png>     ← 两张并排，中间一条竖线
//
// 两张图的 x/y/w/h 是共用的，所以对比"原图 vs 抠图"时对齐是天然的。
// 透明像素会合成到白底和灰底相间的棋盘格上（这样半透明边缘看得见）。

using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

class Zoom
{
    static void Main(string[] argv)
    {
        try
        {
            Run(argv);
        }
        catch (Exception ex)
        {
            Console.WriteLine("失败: " + ex.Message);
            Environment.ExitCode = 1;
        }
    }

    static void Run(string[] argv)
    {
        // 一张图 7 个参数，两张图 8 个（前面多一个图路径）
        bool two = (argv.Length >= 8);
        int need = two ? 8 : 7;
        if (argv.Length < need)
        {
            Console.WriteLine("用法: Zoom.exe <图> [图B] <x> <y> <w> <h> <倍数> <输出.png>");
            return;
        }
        string pathA = argv[0];
        string pathB = two ? argv[1] : null;
        int b = two ? 2 : 1;
        int x = int.Parse(argv[b], CultureInfo.InvariantCulture);
        int y = int.Parse(argv[b + 1], CultureInfo.InvariantCulture);
        int w = int.Parse(argv[b + 2], CultureInfo.InvariantCulture);
        int h = int.Parse(argv[b + 3], CultureInfo.InvariantCulture);
        int mag = int.Parse(argv[b + 4], CultureInfo.InvariantCulture);
        string outPath = argv[b + 5];
        if (mag < 1) mag = 1;

        int srcW, srcH;
        byte[] pxA = LoadRaw(pathA, out srcW, out srcH);
        pxA = Crop(pxA, srcW, srcH, x, y, w, h);

        int gap = two ? 4 : 0;
        int outW = (two ? (w * 2 + gap) : w) * mag;
        int outH = h * mag;
        byte[] outPx = new byte[outW * outH * 4];

        Composite(pxA, w, h, mag, outPx, outW, 0);
        if (two)
        {
            int bw, bh;
            byte[] pxB = LoadRaw(pathB, out bw, out bh);
            pxB = Crop(pxB, bw, bh, x, y, w, h);
            Composite(pxB, w, h, mag, outPx, outW, (w + gap) * mag);
        }

        Common.SavePngBgra(outPath, outW, outH, outPx);
        Console.WriteLine("已写出 " + Path.GetFullPath(outPath) + "  (" + outW + "x" + outH +
                          ", 裁 (" + x + "," + y + ") " + w + "x" + h + " 放大 " + mag + " 倍)");
    }

    static byte[] LoadRaw(string path, out int w, out int h)
    {
        BitmapDecoder dec = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource src = dec.Frames[0];
        FormatConvertedBitmap conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        w = conv.PixelWidth;
        h = conv.PixelHeight;
        byte[] px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);
        return px;
    }

    // 裁一块；越界的地方用透明填
    static byte[] Crop(byte[] src, int srcW, int srcH, int x, int y, int w, int h)
    {
        byte[] dst = new byte[w * h * 4];
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                int sx = x + i, sy = y + j;
                if (sx < 0 || sy < 0 || sx >= srcW || sy >= srcH) continue;
                int s = (sy * srcW + sx) * 4;
                int d = (j * w + i) * 4;
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
                dst[d + 3] = src[s + 3];
            }
        }
        return dst;
    }

    const int CheckSize = 8;

    static void Composite(byte[] src, int srcW, int srcH, int mag, byte[] dst, int dstW, int dstX)
    {
        for (int j = 0; j < srcH * mag; j++)
        {
            for (int i = 0; i < srcW * mag; i++)
            {
                int sx = i / mag, sy = j / mag;
                int s = (sy * srcW + sx) * 4;
                int a = src[s + 3];
                int br = src[s + 2], bg = src[s + 1], bb = src[s];
                // 棋盘底：白 / 浅灰 —— 半透明边缘一眼看得见
                int cr = ((dstX / mag + i) / CheckSize + j / CheckSize) % 2 == 0 ? 255 : 205;
                int cg = cr, cb = cr;
                int r = (br * a + cr * (255 - a)) / 255;
                int g = (bg * a + cg * (255 - a)) / 255;
                int bl = (bb * a + cb * (255 - a)) / 255;
                int d = (j * dstW + dstX + i) * 4;
                dst[d] = (byte)bl;
                dst[d + 1] = (byte)g;
                dst[d + 2] = (byte)r;
                dst[d + 3] = 255;
            }
        }
    }

}
