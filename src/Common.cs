// Common.cs —— 被多个工具共用的代码
//
// 为什么是「共用源码」而不是「共用 DLL」：
//   这个项目的 6 个 exe 是**各自独立编译、能单独拷走用**的（零安装、零依赖）。
//   做成 Common.dll 的话 bin\ 里就多一个必须跟着走的文件，破坏了
//   "每个工具都能单独拿走"这个优点。
//   而 csc 支持一次编译多个源文件 —— 把本文件加到各自的编译行里，
//   每个 exe 里就有一份自己的副本，**外部依赖依然是零**。见 build.sh。
//
// 为什么不能编进 PetNotify.exe：
//   本文件用到 WPF 类型（BitmapSource 等），而 PetNotify 跑在 hook 热路径上，
//   刻意不引用 WPF 来加快启动。它没有任何图片操作，也用不上这里的东西。
//
// 合并前这些代码在四个文件里各写了一遍（PNG 保存 5 处、建目录 4 处），
// 改一处就得记得改另外几处 —— 属于"改一边忘一边"的隐患，不是纯粹的难看。

using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class Common
{
    // 建出目标文件所在的目录。原来 Cutout/MakeSprites/Zoom 各写了一遍，
    // SheetGen 里叫 EnsureDir，四处逐字相同。
    public static void EnsureDir(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir.Length > 0 && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }

    // 存一张 BitmapSource 成 PNG。
    // 注意操作顺序（建编码器 → 加帧 → 建目录 → 写文件）和最原始的实现一致：
    // 顺序不影响输出的字节，但保持一致能让"新旧 A/B 比字节"这个验证更有说服力。
    public static void SavePng(string path, BitmapSource src)
    {
        PngBitmapEncoder enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        EnsureDir(path);
        using (FileStream fs = File.Create(path))
        {
            enc.Save(fs);
        }
    }

    // 存一块原始 BGRA 像素。Cutout 和 Zoom 原来各有一份**逐字相同**的实现。
    public static void SavePngBgra(string path, int w, int h, byte[] bgra)
    {
        SavePng(path, BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4));
    }
}
