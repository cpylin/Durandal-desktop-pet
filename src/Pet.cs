// Pet.cs —— 桌宠主程序
//
// 职责：
//   * 一个透明、无边框、置顶、不抢焦点的窗口
//   * 从 assets/pet.json + pet.png 读取精灵图并播放动画
//   * 监听 UDP，根据 PetNotify.exe 转发来的事件切换状态
//   * 拖拽移动、位置记忆、点击互动、右键菜单
//
// 设计要点：
//   * 点它不抢焦点 = ShowActivated=false + WS_EX_NOACTIVATE 样式 + 窗口过程里把
//     WM_MOUSEACTIVATE 回成 MA_NOACTIVATE。三个都要（少一个都拦不住，细节见
//     DontStealFocusOnClick 上方）。唯一例外是**右键菜单打开期间**：
//     那时会把 NOACTIVATE 临时撤掉，否则菜单点空白处关不掉（见 MenuOpenedAllowActivate）。
//   * 状态有自动衰减：done/error/awake 播完会自己回到 idle，
//     这样即使 hook 丢事件，宠物也不会永远卡在一个姿势
//   * 所有配置写在 exe 同级的 config.json，全部留在 D 盘

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

class Pet
{
    // ---------- 可调参数 ----------
    // 画布尺寸不再写死成正方形：以 assets\pet.json 里的 frameWidth/frameHeight 为准
    // （那是 sheet 里每格的**像素**尺寸），除以 sheetScale 才是屏幕上的逻辑尺寸。
    // 下面的 BaseFrame 只在清单缺字段时当兜底默认值用。
    const int BaseFrame = 128;      // 兜底：manifest 里没写 frameWidth/frameHeight 时用
    // 底部气泡的区域（逻辑像素）。**这两个数直接决定"一句话放不放得下"**，算一下：
    //   窗口宽 = 帧宽/sheetScale + SideMargin = 288/2 + 28 = 172
    //   气泡可用宽 = (172 - 8) * scale - 左右 padding(14) ≈ 150px
    //   字号 11.5 → 一行约 13 个汉字；LabelHeight 42 够放**两行** → 约 26 字
    // 台词最长的（「是幽兰黛尔，也是卡斯兰娜。」）是 14 字，一行差一点、换成两行绰绰有余。
    // 2026-09-13 之前是 16 / 28（一行 12 字、**只能放一行**），
    // 于是 12 字以上全被截成"…" —— 就是用户报的"部分语句显示不完全"。
    const int LabelHeight = 42;     // 底部气泡占的高度：够放两行
    const int SideMargin = 28;      // 窗口比人物宽出来的量，给气泡留位置
    const int DefaultPort = 47821;  // UDP 端口，与 PetNotify 保持一致
    // "右下角"是四处的共同落点：启动默认位置、被召唤时挪回来、菜单里的「回到右下角」、
    // 以及启动失败的提示卡片。以前这个 30 是散在 8 行里的字面量，改一处就会不一致。
    const int CornerMargin = 30;    // 离屏幕右下角留的余量（逻辑像素）
    const int MinVisibleEdge = 40;  // 拖到屏幕外时至少留这么多像素露在外面（见 ClampToScreen）
    // 判定"这一下是单击还是拖动"：按下到松开的窗口位移小于它就算单击（→ SayLine 说句话）。
    // 量的是**曼哈顿距离** |dx|+|dy|，不是欧氏距离。调大 = 手抖也被当单击。
    const int ClickSlop = 4;

    // 状态自动衰减（毫秒）。alert 不衰减——它表示"在等你确认"，要一直举着手。
    static readonly Dictionary<string, double> DecayMs = new Dictionary<string, double>();
    static Pet()
    {
        DecayMs["awake"] = 2500;
        DecayMs["done"]  = 3500;
        DecayMs["error"] = 5000;
    }
    const double StaleMs = 90000;   // 工作/思考中但 90 秒没动静 -> 判定会话已死，回 idle
    const double DefaultDecayMs = 3000;   // 非循环状态没登记在 DecayMs 里时的默认回落时长

    // 单调时钟（毫秒）。
    // 原来用的是 Environment.TickCount：32 位、**49.7 天回绕**，而"开机自启 + 长期不重启"
    // 正好会撞上（Windows 的快速启动不重置这个计数）。回绕会让"过了多久"算成负数或巨值，
    // 症状是动画不再衰减、气泡不再消失 —— 而且极难复现。Stopwatch 不回绕。
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    static double NowMs() { return Clock.Elapsed.TotalMilliseconds; }

    // ---------- 状态 ----------
    Window win;
    Image img;
    Border labelBox;
    TextBlock labelText;
    ScaleTransform labelPop;         // 气泡弹出的缩放（见 ShowLabel）；平时是恒等变换

    Dictionary<string, Anim> anims = new Dictionary<string, Anim>();
    string state = "idle";
    string pendingState = null;      // 非循环动画播完后要切到的状态
    double labelHideAt = 0;          // NowMs() 时间戳

    double scale = 1.0;
    double opacity = 0.92;
    double posX = double.NaN;
    double posY = double.NaN;

    // 全屏游戏时自动躲开（判定见 IsFullscreenForeground）。默认**开**。
    // 缺字段时也要是 true：老 config.json 里没有这一项，默认值给错就等于功能没上。
    bool fullscreenAutoHide = true;
    bool petHidden = false;   // 当前是否被**本功能**藏起来了（用来区分"本来就该显示"）
    int fullscreenStreak = 0; // 连续同向采样计数，防抖用

    // 用户主动「隐藏到托盘」的状态。**必须和 petHidden 分开**：两者语义不同，
    // 共用一个字段的话，你手动藏起来之后切出全屏，全屏那套"恢复"逻辑会把宠物弹回来。
    // 不写进 config.json —— 下次启动一律可见（藏起来的宠物重启后还是不出现，
    // 用户会以为"双击没反应"，这是这个项目最忌讳的失败方式）。
    bool hiddenByUser = false;
    System.Windows.Forms.NotifyIcon trayIcon;   // 懒创建：第一次隐藏时才建

    // 画布尺寸：LoadSprites 从 manifest 读出来存这里（以前读完就丢，所以只能是正方形）
    int frameW = BaseFrame;
    int frameH = BaseFrame;
    double sheetScale = 1.0;        // 素材像素密度：屏幕尺寸 = frameW / sheetScale

    int port = DefaultPort;
    string rootDir;
    string configPath;

    DispatcherTimer animTimer;
    DispatcherTimer tickTimer;
    DispatcherTimer fullscreenTimer;   // 全屏检测，独立于 tickTimer（见 BuildWindow）
    int frameIndex = 0;
    double lastEventTick = 0;

    ContextMenu openMenu;   // 当前打开的右键菜单（点宠物时先把它收起来）

    // ================= 入口 =================
    [STAThread]
    static void Main(string[] argv)
    {
        // 调试模式：把窗口内容离屏渲染成 PNG，用来检查布局和素材对齐。
        //   Pet.exe --shot <状态> <输出路径> [气泡文字]
        if (argv.Length >= 1 && argv[0] == "--shot")
        {
            try
            {
                Pet p = new Pet();
                p.ShotMode(
                    argv.Length >= 2 ? argv[1] : "idle",
                    argv.Length >= 3 ? argv[2] : "shot.png",
                    argv.Length >= 4 ? argv[3] : null);
            }
            catch (Exception ex)
            {
                Console.WriteLine("渲染失败: " + ex.Message);
            }
            return;
        }

        // 单实例保护：重复启动不会开出第二只，而是给已经在跑的那只发一次"召唤"，
        // 让它冒个泡 + 如果跑到屏幕外就自己挪回来。
        //
        // 这里必须等 ack。等不到说明对面虽然占着单实例锁，却没在监听（卡住、
        // 或端口被别的程序占了）——那时候如果也静默退出，用户看到的就是
        // "双击完全没反应"，正是这个设计以前最坑的地方。
        bool created;
        Mutex mtx = new Mutex(true, "ClaudePet_SingleInstance", out created);
        if (!created)
        {
            if (!SendRaw("locate||", 1500))
            {
                ShowNotice(
                    "已经有一只桌宠在运行，但它没有回应。\n\n" +
                    "可能是它卡住了，或者 " + DefaultPort + " 端口被别的程序占用。\n" +
                    "请在任务管理器里结束 Pet.exe，然后再双击一次。");
            }
            return;
        }

        try
        {
            Pet app = new Pet();
            app.Run();
        }
        catch (Exception ex)
        {
            // 同样不能用 MessageBox：启动失败时它一闪就没了，用户看到的就是
            // "双击了但什么都没发生"——等于把真正的错误信息藏了起来。
            ShowNotice("桌宠启动失败：\n\n" + ex.Message);
        }
        finally
        {
            GC.KeepAlive(mtx);
        }
    }

    void Run()
    {
        rootDir = FindRoot();
        configPath = Path.Combine(rootDir, "config.json");
        LoadConfig();

        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;

        BuildWindow();
        LoadSprites();
        StartUdpListener();

        SetState("idle");
        win.Show();
        DontStealFocusOnClick(win);

        // 到这里窗口尺寸才是最终值（manifest 驱动的尺寸在 LoadSprites 里算好、
        // Show 之后才真正生效），所以这时候判断"看不看得全"才准。
        // 存下来的坐标可能已经跑到屏幕外（改过分辨率、拔过显示器），
        // 那种情况下宠物会一声不吭地活在屏幕外 —— 看着就像"程序根本没启动"。
        EnsureVisible();

        fullscreenTimer.Start();   // 全屏检测（窗口已经显示出来了，才能 Hide/Show）

        // 退出前必须收掉托盘图标，否则进程没了图标还留在托盘里（要鼠标划过去才消失）。
        // 两条退出路径都要盖住：app.Exit 管 Application.Current.Shutdown()（菜单里的「退出」），
        // win.Closed 管直接关窗口那条。DisposeTray 是幂等的，重复调用无害。
        app.Exit += delegate(object s, ExitEventArgs e) { DisposeTray(); };
        win.Closed += delegate(object s, EventArgs e) { DisposeTray(); };

        app.Run();
        DisposeTray();      // 兜底
        SaveConfig();
    }

    // 离屏渲染：不显示窗口，直接把 Grid 画到 PNG。
    // 用于验证布局（尤其是精灵图和气泡的相对位置）与换素材后的对齐。
    void ShotMode(string st, string outPath, string label)
    {
        rootDir = FindRoot();
        configPath = Path.Combine(rootDir, "config.json");

        BuildWindow();
        LoadSprites();
        SetState(st);
        if (label != null && label.Length > 0) ShowLabel(label);

        FrameworkElement root = (FrameworkElement)win.Content;
        root.Measure(new Size(win.Width, win.Height));
        root.Arrange(new Rect(0, 0, win.Width, win.Height));
        root.UpdateLayout();

        const double sc = 4.0;   // 放大 4 倍渲染，便于肉眼检查边缘
        int pw = (int)Math.Round(win.Width * sc);
        int ph = (int)Math.Round(win.Height * sc);

        RenderTargetBitmap rtb = new RenderTargetBitmap(pw, ph, 96.0 * sc, 96.0 * sc, PixelFormats.Pbgra32);
        rtb.Render(root);

        // 这里以前是内联的"建编码器 → 加帧 → 写文件"。改用 Common.SavePng 之后
        // 多了一个副作用：**目标目录不存在时会自动建**（原来会直接抛异常）。
        // 对 --shot 这种调试用法人手一个目录很正常，建出来比报错好。
        Common.SavePng(outPath, rtb);

        // 关键尺寸写到同名的 .txt —— winexe 没有控制台，只能这样带出诊断信息
        StringBuilder diag = new StringBuilder();
        diag.AppendLine("渲染文件   : " + outPath + "  (" + pw + "x" + ph + ", 放大 " + sc + " 倍)");
        diag.AppendLine("窗口逻辑尺寸: " + win.Width + " x " + win.Height);
        diag.AppendLine("精灵图元素 : " + img.ActualWidth + " x " + img.ActualHeight +
                        "   位置=(" + Math.Round(img.TranslatePoint(new Point(0, 0), root).X) + "," +
                        Math.Round(img.TranslatePoint(new Point(0, 0), root).Y) + ")");
        diag.AppendLine("气泡元素   : " + Math.Round(labelBox.ActualWidth) + " x " +
                        Math.Round(labelBox.ActualHeight) +
                        "   位置=(" + Math.Round(labelBox.TranslatePoint(new Point(0, 0), root).X) + "," +
                        Math.Round(labelBox.TranslatePoint(new Point(0, 0), root).Y) + ")" +
                        "   不透明度=" + labelBox.Opacity);
        diag.AppendLine("气泡文字   : " + labelText.Text);
        File.WriteAllText(outPath + ".txt", diag.ToString(), new UTF8Encoding(false));
    }

    // exe 在 bin/ 下，素材在上一级的 assets/；两种布局都兼容
    static string FindRoot()
    {
        string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string[] candidates = new string[] {
            exeDir,
            Path.GetFullPath(Path.Combine(exeDir, ".."))
        };
        foreach (string c in candidates)
        {
            if (File.Exists(Path.Combine(c, "assets", "pet.json"))) return c;
        }
        return exeDir;
    }

    // ================= 窗口 =================
    void BuildWindow()
    {
        win = new Window();
        win.WindowStyle = WindowStyle.None;
        win.AllowsTransparency = true;
        win.Background = Brushes.Transparent;
        win.Topmost = true;
        win.ShowInTaskbar = false;
        win.ShowActivated = false;          // 不抢焦点
        win.ResizeMode = ResizeMode.NoResize;
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Title = "Claude Pet";

        // 行高必须显式指定：两个都不设的话默认各占一半（star），
        // 128px 的精灵图会被塞进 78px 的槽位而被裁掉一半。
        Grid grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions[0].Height = GridLength.Auto;                    // 精灵图：按图片实际高度
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star); // 气泡：吃掉剩余高度

        img = new Image();
        img.Stretch = Stretch.Uniform;
        img.HorizontalAlignment = HorizontalAlignment.Center;
        img.VerticalAlignment = VerticalAlignment.Top;
        img.IsHitTestVisible = false;
        Grid.SetRow(img, 0);
        grid.Children.Add(img);

        labelText = new TextBlock();
        labelText.FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        labelText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x33, 0x44));
        // **换行**而不是直接截断：台词超过一行时折到第二行，而不是变成"…"。
        // TextTrimming 留着当**最后一道保险** —— 配上 ApplyScale 里设的 MaxHeight，
        // 万一来了长到两行也放不下的文本（比如项目名特别长的"会话开始 · xxx"），
        // 它会显示省略号，而不是被 Grid 悄悄裁掉半行。
        labelText.TextWrapping = TextWrapping.Wrap;
        labelText.TextTrimming = TextTrimming.CharacterEllipsis;
        labelText.HorizontalAlignment = HorizontalAlignment.Center;
        labelText.VerticalAlignment = VerticalAlignment.Center;

        labelBox = new Border();
        labelBox.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        labelBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x5C, 0x94));
        labelBox.BorderThickness = new Thickness(1.4);
        labelBox.CornerRadius = new CornerRadius(9);
        labelBox.Padding = new Thickness(7, 2, 7, 2);
        labelBox.Child = labelText;
        labelBox.Opacity = 0;
        labelBox.HorizontalAlignment = HorizontalAlignment.Center;
        labelBox.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(labelBox, 1);
        labelBox.Margin = new Thickness(0, 3, 0, 0);
        // 气泡"弹出"动画的载体：从下方中心放大着冒出来（原点是底部中心 = 角色头顶位置）。
        // 平时它是恒等变换；只有左键说话时才跑一次（见 ShowLabel 的 animate 参数）。
        labelPop = new ScaleTransform(1, 1);
        labelBox.RenderTransform = labelPop;
        labelBox.RenderTransformOrigin = new Point(0.5, 1.0);
        grid.Children.Add(labelBox);

        win.Content = grid;

        win.MouseLeftButtonDown += OnMouseDown;
        win.MouseRightButtonUp += OnRightClick;

        ApplyScale(scale);
        win.Opacity = opacity;

        if (!double.IsNaN(posX) && !double.IsNaN(posY))
        {
            win.Left = posX;
            win.Top = posY;
        }
        else
        {
            // 默认停在右下角
            win.Left = SystemParameters.WorkArea.Right - win.Width - CornerMargin;
            win.Top = SystemParameters.WorkArea.Bottom - win.Height - CornerMargin;
        }

        // 注意：这里**不**做"看不看得全"的判断。
        // 原因是此时窗口尺寸还没最终确定（LoadSprites 之后才会按 manifest 重算一次），
        // 曾经因此按一个错误的尺寸把宠物挪到了右下角。改在 win.Show() 之后判断。

        animTimer = new DispatcherTimer();
        animTimer.Tick += delegate(object s, EventArgs e) { NextFrame(); };

        tickTimer = new DispatcherTimer();
        tickTimer.Interval = TimeSpan.FromMilliseconds(500);
        tickTimer.Tick += delegate(object s, EventArgs e) { Tick(); };
        tickTimer.Start();

        // 全屏检测用**独立**定时器：不要挂进 Tick()，那是状态衰减逻辑，混在一起以后
        // 改哪边都可能踩到另一边。
        // 这里只创建、不启动 —— ShotMode() 也会走到 BuildWindow()，而那个模式从不显示窗口，
        // 启动检测会去 Hide/Show 一个压根没显示过的窗口。真正的 Start() 在 Run() 里。
        fullscreenTimer = new DispatcherTimer();
        fullscreenTimer.Interval = TimeSpan.FromMilliseconds(500);
        fullscreenTimer.Tick += delegate(object s2, EventArgs e2) { CheckFullscreen(); };

        // 右键单/双击的判定定时器，见 OnRightClick。
        rightClickTimer = new DispatcherTimer();
        rightClickTimer.Interval = TimeSpan.FromMilliseconds(RightDblClickMs);
        rightClickTimer.Tick += delegate(object s3, EventArgs e3)
        {
            rightClickTimer.Stop();     // 到点了还没有第二下 → 判定为单击
            CycleState();
        };
    }

    void ApplyScale(double s)
    {
        scale = s;
        if (s < 0.3) scale = 0.3;
        if (s > 3.0) scale = 3.0;

        // 屏幕上的逻辑尺寸 = 素材像素尺寸 / 像素密度（frameW/sheetScale）。
        // 2 倍素材在 100% DPI 下是超采样、放大到 2 倍也不糊，代价只是文件大一点。
        double dispW = frameW / sheetScale;
        double dispH = frameH / sheetScale;

        img.Width = dispW * scale;
        img.Height = dispH * scale;

        double winW = dispW + SideMargin;
        win.Width = winW * scale;
        win.Height = (dispH + LabelHeight) * scale;

        labelText.FontSize = Math.Max(8.5, 11.5 * scale);
        labelBox.MaxWidth = (winW - 8) * scale;
        // 文字的**高度**上限也要卡住，否则 TextTrimming 不会生效：TextBlock 会一直长高、
        // 然后被 Grid 那一行悄悄裁掉半行（看起来就是"字被切了"）。留 6px 给上下 padding。
        // 注意字号有个 8.5 的下限（缩得太小看不清），所以**窗口缩小以后每行能放的字数会变少** ——
        // 这就是为什么光靠"加宽"不够，还得允许折行。
        labelText.MaxHeight = LabelHeight * scale - 6;

        ClampToScreen();
    }

    void ClampToScreen()
    {
        if (double.IsNaN(win.Left)) return;
        double maxL = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - MinVisibleEdge;
        double maxT = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - MinVisibleEdge;
        if (win.Left < SystemParameters.VirtualScreenLeft - win.Width + MinVisibleEdge)
            win.Left = SystemParameters.VirtualScreenLeft - win.Width + MinVisibleEdge;
        if (win.Top < SystemParameters.VirtualScreenTop)
            win.Top = SystemParameters.VirtualScreenTop;
        if (win.Left > maxL) win.Left = maxL;
        if (win.Top > maxT) win.Top = maxT;
    }

    // 被"召唤"时用（双击 Pet.exe，见 Main）：先确认自己看得见，再冒个泡。
    // 有了它，"重复双击"就永远不会再表现为"毫无反应"。
    void SummonSelf()
    {
        // 被「全屏自动隐藏」藏起来时，双击 exe 是**唯一够得着的救生圈** ——
        // 藏起来之后你右键不到它，菜单里那个开关等于够不着。
        // 所以这里除了叫回来，还顺手把功能关掉：否则下一轮采样又把它藏回去，
        // 用户会觉得"双击没用"。关掉是确定的，气泡会说明，想开再右键勾回来。
        string extra = "";
        if (hiddenByUser)
        {
            // 手动藏到托盘之后，双击 exe 和托盘图标一样是够得着的出口
            ShowFromTray();
            extra = "（已从托盘回来）";
        }
        else if (petHidden)
        {
            ShowPetAfterFullscreen();
            if (fullscreenAutoHide)
            {
                fullscreenAutoHide = false;
                fullscreenStreak = 0;
                SaveConfig();
                extra = "（已关闭全屏自动隐藏）";
            }
        }

        EnsureVisible();
        SetState("awake");              // 蹦一下，像被叫到
        ShowLabel("我在这儿！" + extra);
    }

    // 被"召唤"时先确认自己看得见：看不全就挪回右下角（和右键菜单那个「回到右下角」同一个位置）。
    //
    // 阈值是"完全可见"而不是"露出一点就算，千万别用后者——
    // ClampToScreen 是宽容的，配置里一个屏幕外坐标会被它夹成"只露 40px 的一条边"，
    // 而那在肉眼看来跟彻底消失没区别，正是用户会跑来双击 Pet.exe 的原因。
    void EnsureVisible()
    {
        if (double.IsNaN(win.Left) || double.IsNaN(win.Top)) return;

        double vl = SystemParameters.VirtualScreenLeft;
        double vt = SystemParameters.VirtualScreenTop;
        double vr = vl + SystemParameters.VirtualScreenWidth;
        double vb = vt + SystemParameters.VirtualScreenHeight;

        bool fullyVisible = (win.Left >= vl) && (win.Top >= vt) &&
                            (win.Left + win.Width <= vr) && (win.Top + win.Height <= vb);
        if (fullyVisible) return;

        Rect wa = SystemParameters.WorkArea;
        win.Left = wa.Right - win.Width - CornerMargin;
        win.Top = wa.Bottom - win.Height - CornerMargin;
        SaveConfig();
    }

    // ================= 素材 =================
    class Anim
    {
        public int Row;
        public int Frames;
        public double Fps;
        public bool Loop;
        public CroppedBitmap[] Bitmaps;
    }

    void LoadSprites()
    {
        string manifestPath = Path.Combine(rootDir, "assets", "pet.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("找不到素材清单：" + manifestPath +
                "\n请先运行 bin\\MakeSprites.exe 生成占位素材。");

        JavaScriptSerializer ser = new JavaScriptSerializer();
        Dictionary<string, object> m =
            ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(manifestPath, Encoding.UTF8));

        // 缺字段要用兜底值，不能直接下标取 —— 字典直接取会抛 KeyNotFoundException，
        // 于是 ToInt/ToDouble 那两个默认值参数永远用不上（以前就是这样，注释写着有兜底，
        // 实际一缺字段就报"给定关键字不在字典中"）。
        int fw = ToInt(m.ContainsKey("frameWidth") ? m["frameWidth"] : null, BaseFrame);
        int fh = ToInt(m.ContainsKey("frameHeight") ? m["frameHeight"] : null, BaseFrame);
        string sheetName = m.ContainsKey("sheet") ? Convert.ToString(m["sheet"]) : "pet.png";

        double newSheetScale = m.ContainsKey("sheetScale") ? ToDouble(m["sheetScale"], 1.0) : 1.0;
        if (newSheetScale < 0.1) newSheetScale = 1.0;

        string sheetPath = Path.Combine(rootDir, "assets", sheetName);
        BitmapImage sheet = new BitmapImage();
        sheet.BeginInit();
        sheet.UriSource = new Uri(sheetPath, UriKind.Absolute);
        sheet.CacheOption = BitmapCacheOption.OnLoad;   // 立即加载，不锁文件
        sheet.EndInit();
        sheet.Freeze();

        // **先全部读进局部变量，成功之后才换上去。**
        // 以前是边解析边改字段：中途任何一步抛异常（清单写错、sheet 比清单小、
        // 缺 idle），就会留下"半套状态"——anims 空了但 state 还是 idle，
        // 下一个 hook 事件进 SetState 就抛 KeyNotFoundException，
        // 于是宠物直接消失：没有窗口、没有提示、没有日志。那是这个项目最糟的失败模式。
        Dictionary<string, Anim> newAnims = new Dictionary<string, Anim>();
        Dictionary<string, object> defs =
            (Dictionary<string, object>)m["animations"];

        foreach (KeyValuePair<string, object> kv in defs)
        {
            Dictionary<string, object> d = (Dictionary<string, object>)kv.Value;

            Anim a = new Anim();
            a.Row = ToInt(d.ContainsKey("row") ? d["row"] : null, 0);
            a.Frames = ToInt(d.ContainsKey("frames") ? d["frames"] : null, 1);
            a.Fps = ToDouble(d.ContainsKey("fps") ? d["fps"] : null, 8);
            a.Loop = !d.ContainsKey("loop") || Convert.ToBoolean(d["loop"]);

            a.Bitmaps = new CroppedBitmap[a.Frames];
            for (int i = 0; i < a.Frames; i++)
            {
                Int32Rect r = new Int32Rect(i * fw, a.Row * fh, fw, fh);
                // 越界时 WPF 的原文是"值不在预期的范围内"，完全看不出是清单和素材对不上。
                // 自己先检查一遍，给一句能照着修的提示（手改清单时最容易踩这个）。
                if (r.X + r.Width > sheet.PixelWidth || r.Y + r.Height > sheet.PixelHeight)
                {
                    throw new InvalidDataException(
                        "素材清单和 " + sheetName + " 对不上：状态 \"" + kv.Key + "\" 需要第 " + a.Row +
                        " 行、共 " + a.Frames + " 帧，切图范围超出了素材实际尺寸 " +
                        sheet.PixelWidth + "x" + sheet.PixelHeight + "。\n" +
                        "请检查清单里的 row / frames / frameWidth / frameHeight 是否和素材图一致" +
                        "（最省事的办法是用 SheetGen.exe 重新生成一遍，它会自己写清单）。");
                }
                CroppedBitmap cb = new CroppedBitmap(sheet, r);
                cb.Freeze();
                a.Bitmaps[i] = cb;
            }
            newAnims[kv.Key] = a;
        }

        if (!newAnims.ContainsKey("idle"))
            throw new InvalidDataException("素材清单里缺少 idle 动画。");

        // 到这里才算成功：一次性换上新的一套，旧的那套原封不动。
        // （「重新载入素材」失败时，宠物会继续按旧的正常跑 —— 而不是变成一具空壳等下次事件来炸。）
        anims = newAnims;
        frameW = fw;
        frameH = fh;
        sheetScale = newSheetScale;

        // 素材尺寸可能和上次不一样（换了形象的画布大小），所以这里重算一次窗口尺寸。
        // 右键菜单的「重新载入素材」也走这条路，于是换素材后不用重启就能生效。
        ApplyScale(scale);
    }

    static int ToInt(object o, int dflt)
    {
        if (o == null) return dflt;
        try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); }
        catch (Exception) { return dflt; }
    }

    static double ToDouble(object o, double dflt)
    {
        if (o == null) return dflt;
        try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }
        catch (Exception) { return dflt; }
    }

    // Convert.ToBoolean 遇到 "yes" / "1" 这类非 true/false 字符串会**抛异常**，
    // 而 LoadConfig 是整体 try/catch 的 —— 也就是说写坏了这一项会把 port、scale、坐标
    // 一起悄悄退回默认值。所以布尔也要有自己的兜底，跟 ToInt/ToDouble 一个道理。
    static bool ToBool(object o, bool dflt)
    {
        if (o == null) return dflt;
        try { return Convert.ToBoolean(o, CultureInfo.InvariantCulture); }
        catch (Exception) { return dflt; }
    }

    // ================= 动画 =================
    void SetState(string s)
    {
        // 「隐藏」不是一个姿势 —— 它没有素材。所以在这里就分流掉，
        // 免得掉进下面"清单里没有就退回 idle"的兜底里（那样点了隐藏反而变成待机）。
        if (s == "hidden") { HideToTray(); return; }

        if (s == null || s.Length == 0) s = "idle";
        if (!anims.ContainsKey(s)) s = "idle";
        // 连 idle 都没有（素材加载失败留下的空壳）就什么都别做 ——
        // 宁可姿势不动，也不能在事件线程上抛异常让进程消失。
        if (!anims.ContainsKey(s)) return;

        // 同一个循环状态重复设置就不重画 —— 但**必须先刷新"活着"的时间戳**。
        // 判断会话是否还活着看的是"有没有事件在来"，不是"状态有没有变"：
        // 原来这条早退把连续到来的同一个状态排除在外，于是跑一个超过 90 秒的工具时，
        // 明明事件还在来，宠物却会误判成会话已死、掉回 idle，下一个事件又跳回去。
        if (s == state && anims[s].Loop && img.Source != null)
        {
            lastEventTick = NowMs();
            return;
        }

        state = s;
        frameIndex = 0;
        lastEventTick = NowMs();
        UpdateTrayText();   // 托盘提示跟着状态走（没建托盘图标时是空操作）

        Anim a = anims[state];
        animTimer.Stop();
        animTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(30, 1000.0 / Math.Max(0.5, a.Fps)));
        // 被藏起来的时候（不管是全屏自动隐藏还是用户手动藏到托盘）不要开动画：
        // 看不见的东西没必要画 —— 打游戏时恰恰最在意这点 CPU。
        // （重新显示时分别由 ShowPetAfterFullscreen() / ShowFromTray() 开起来。）
        // 注意状态本身照常更新 —— 只是不画，回来时姿势是对的。
        if (!petHidden && !hiddenByUser) animTimer.Start();

        img.Source = a.Bitmaps[0];

        // 非循环动画播完要回到哪
        if (!a.Loop)
        {
            string back = "idle";
            if (state == "error") back = "idle";
            pendingState = back;
        }
        else
        {
            pendingState = null;
        }
    }

    void NextFrame()
    {
        Anim a;
        if (!anims.TryGetValue(state, out a)) return;

        frameIndex++;
        if (frameIndex >= a.Frames)
        {
            if (a.Loop)
            {
                frameIndex = 0;
            }
            else
            {
                // 停在最后一帧，交给 Tick() 决定何时切回
                frameIndex = a.Frames - 1;
                animTimer.Stop();
                img.Source = a.Bitmaps[frameIndex];
                return;
            }
        }
        img.Source = a.Bitmaps[frameIndex];
    }

    // ================= 状态衰减 =================
    void Tick()
    {
        double now = NowMs();

        // 气泡淡出
        if (labelBox.Opacity > 0 && now > labelHideAt)
        {
            DoubleAnimation fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
            labelBox.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // 非循环动画播完后的回落
        if (pendingState != null && !animTimer.IsEnabled)
        {
            double limit;
            // 表里没有的状态给个默认时长 —— 否则"loop:false 但没登记"的新状态
            // （比如自己加的一个"打招呼 2"）会永远卡在最后一帧上，再也不动。
            if (!DecayMs.TryGetValue(state, out limit)) limit = DefaultDecayMs;
            if ((now - lastEventTick) > limit)
            {
                string back = pendingState;
                pendingState = null;
                SetState(back);
                return;
            }
        }

        // 兜底：卡在 think/work 太久说明会话异常结束，回 idle
        if ((state == "think" || state == "work") && (now - lastEventTick) > StaleMs)
        {
            SetState("idle");
        }
    }

    // ================= UDP 监听 =================
    void StartUdpListener()
    {
        Thread t = new Thread(delegate()
        {
            UdpClient udp = null;
            try
            {
                udp = new UdpClient(port);
                while (true)
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = udp.Receive(ref remote);
                    string msg = Encoding.UTF8.GetString(data);

                    // 另一只 Pet.exe 发来的"召唤"要回 ack —— 它在等这个来判断我们
                    // 是不是活着（见 Main / SendRaw）。从监听线程直接回，不排 UI 队列，
                    // 免得 UI 正忙时对面误判超时弹窗。
                    if (msg != null && msg.Trim().StartsWith("locate"))
                    {
                        byte[] ack = Encoding.UTF8.GetBytes("ack");
                        try { udp.Send(ack, ack.Length, remote); } catch (Exception) { }
                    }

                    win.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                        new Action(delegate() { OnMessage(msg); }));
                }
            }
            catch (Exception)
            {
                // 端口被占（例如开了两只）就静默退出，不弹窗打扰
                if (udp != null) { try { udp.Close(); } catch (Exception) { } }
            }
        });
        t.IsBackground = true;
        t.Name = "pet-udp";
        t.Start();
    }

    void OnMessage(string msg)
    {
        if (msg == null) return;
        string[] parts = msg.Split('|');
        string s = (parts.Length > 0) ? parts[0].Trim() : "idle";
        string tool = (parts.Length > 1) ? parts[1].Trim() : "";
        // 第 4 段是 PetNotify 给的"备注"（目前只有会话开始/结束会带，内容是「会话开始 · 项目名」）。
        // 加这一段是为了：进会话时把上一次残留的姿势（比如一直举着手、或者在睡）真的刷新掉，
        // 并顺便告诉你现在动的是哪个项目。
        string note = (parts.Length > 3) ? parts[3].Trim() : "";

        // "召唤"：有人又双击了一次 Pet.exe（见 Main）。它不是状态，别拿去查白名单。
        if (s == "locate")
        {
            SummonSelf();
            return;
        }

        // 这里**故意不做状态名白名单**。
        // 原来写死了一张 8 个名字的表，结果是：按 README 说的"加个状态、重生成清单、
        // 改 hook 就能用"——新状态名会在这一步被悄悄改写成 idle，动画永远显示不出来，
        // 而且没有任何报错。安全性由 SetState 兜底（清单里没有的名字一律回退 idle），
        // 所以这里放开不会崩，只会让"清单里真有的新状态"能正常用。
        //
        // 工作气泡：ToolLabel 只看工具名，所以同一类工具连续调用时文字是一样的。
        // 这里不去重（注释以前写着"别让气泡一直闪"但实际那条判断永远不成立）——
        // 反复亮一下正是"我还在干活"的信号，衰减掉落更奇怪。

        if (s == "work" && tool.Length > 0)
        {
            ShowLabel(ToolLabel(tool));
        }

        SetState(s);

        // 备注放在最后：它会覆盖上面 work 的"正在干什么"气泡，但备注只在会话开始/结束时才有，
        // 两者不会同时发生。
        if (note.Length > 0) ShowLabel(note);
    }

    static string ToolLabel(string tool)
    {
        if (tool == null) return "";
        // MCP 工具名很长，截断一下
        string t = tool;
        if (t.StartsWith("mcp__", StringComparison.Ordinal))
        {
            string[] seg = t.Split('_');
            if (seg.Length >= 3) t = seg[seg.Length - 1];
        }
        switch (t)
        {
            case "Read":  return "正在读文件";
            case "Write": return "正在写文件";
            case "Edit":  return "正在改代码";
            case "Bash":  return "正在跑命令";
            case "Grep":  return "正在搜索";
            case "Glob":  return "正在找文件";
            case "Task":  return "正在派活";
            case "WebFetch":  return "正在查资料";
            case "WebSearch": return "正在上网搜";
        }
        return "正在用 " + t;
    }

    void ShowLabel(string text) { ShowLabel(text, false); }

    // 显示气泡。animate=true 时播一次"弹出"动画（左键说话用）。
    //
    // **"适应快速点击"靠的是 BeginAnimation 的替换语义**：每次进来先 BeginAnimation(prop, null)
    // 把上一条动画清掉、再重放，于是连点只是不停从头播 —— 不会堆积、不会排队、不需要自己维护队列。
    // （先试过写个动画队列，多余，而且更容易出错。）
    //
    // animate=false 时必须把变换**显式复位**：`--shot` 那条离屏渲染路径走的就是它，
    // 要是残留着上一次的缩放值，截出来的气泡会是缩着的、半透明的 —— 验证就白做了。
    void ShowLabel(string text, bool animate)
    {
        if (text == null || text.Length == 0) return;
        labelText.Text = text;
        labelBox.BeginAnimation(UIElement.OpacityProperty, null);
        labelHideAt = NowMs() + 2600;

        if (animate && labelPop != null)
        {
            labelPop.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            labelPop.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            labelBox.Opacity = 0.0;
            labelPop.ScaleX = 0.72;
            labelPop.ScaleY = 0.72;
            TimeSpan d = TimeSpan.FromMilliseconds(170);
            labelBox.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, d));
            // 稍微过冲一点再回落，比线性放大更像"冒出来"而不是"变出来"
            BackEase ease = new BackEase();
            ease.Amplitude = 0.35;
            ease.EasingMode = EasingMode.EaseOut;
            DoubleAnimation sx = new DoubleAnimation(0.72, 1.0, d);
            sx.EasingFunction = ease;
            DoubleAnimation sy = new DoubleAnimation(0.72, 1.0, d);
            sy.EasingFunction = ease;
            labelPop.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            labelPop.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
        }
        else
        {
            labelBox.Opacity = 1.0;
            if (labelPop != null)
            {
                labelPop.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                labelPop.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                labelPop.ScaleX = 1.0;
                labelPop.ScaleY = 1.0;
            }
        }
    }

    // ================= 交互 =================
    // 拖拽用 win.DragMove()（久经考验），判断"单击还是拖拽"则发生在它**返回之后**：
    // DragMove 是模态循环，吞掉鼠标消息，中途插不进去，只能事后判断。
    //
    // 判断依据必须是**窗口自己的坐标**，不能是 e.GetPosition(win)：
    // DragMove 内部会移动窗口，用"鼠标相对窗口"的前后差会被它污染 ——
    // 实测症状是原地单击被误判成拖拽（点一下宠物，它被挪走而不是切状态）。
    //
    // （试过自己实现拖拽来彻底避开 DragMove 的这个脾气，结果我的版本自己有 bug：
    //   拖完存进 config.json 的位置是错的。宁可留着 DragMove —— 偶尔的抖动
    //   比"拖拽本身不可靠"轻得多。）
    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            // 菜单开着的时候点宠物：先把菜单收起来，这一次不切状态 ——
            // 否则会同时发生"关菜单"和"切状态"两件事，看起来像误触。
            if (openMenu != null && openMenu.IsOpen)
            {
                openMenu.IsOpen = false;
                return;
            }

            double beforeL = win.Left;
            double beforeT = win.Top;

            win.DragMove();      // 阻塞到鼠标松开

            double moved = Math.Abs(win.Left - beforeL) + Math.Abs(win.Top - beforeT);
            if (moved < ClickSlop)
            {
                SayLine();          // 左键单击 = 说话（状态不变；切状态挪到右键了）
            }
            else
            {
                posX = win.Left;
                posY = win.Top;
                SaveConfig();
            }
        }
        catch (Exception)
        {
            // DragMove 在鼠标已释放时会抛异常，忽略
        }
    }

    // 单击：按固定顺序切到下一个**清单里存在**的状态，并把状态名显示在气泡里。
    //
    // 顺序写死在代码里，不依赖 JSON 里 animations 的顺序 —— Dictionary 的枚举顺序
    // 不保证等于写入顺序，靠它排序是撞运气。
    //
    // 注意 alert / sleep 是**持久**状态（举着手等你确认 / 睡着了），点进去之后会停在那里，
    // 那是它们本来的语义，再点一下就继续往下走。下一个 Claude 事件也会把它拉回正确的状态。
    static readonly string[] CycleOrder = new string[] {
        "idle", "think", "work", "alert", "done", "awake", "sleep", "error"
    };

    void CycleState()
    {
        // 实际能轮到的状态 = 上面那个顺序里清单有的，**再加上清单里有但没写进顺序的**
        // （以后自己加了新状态，只要清单里有，就自动排在最后能点到，不用回来改这个数组）。
        List<string> order = new List<string>();
        for (int i = 0; i < CycleOrder.Length; i++)
        {
            if (anims.ContainsKey(CycleOrder[i])) order.Add(CycleOrder[i]);
        }
        foreach (KeyValuePair<string, Anim> kv in anims)
        {
            if (!order.Contains(kv.Key)) order.Add(kv.Key);
        }
        if (order.Count == 0) return;

        int cur = order.IndexOf(state);        // 当前不在列表里时是 -1
        string next = order[(cur + 1) % order.Count];
        SetState(next);
        ShowLabel(StateLabel(next));
    }

    // ================= 左键说话 =================
    // 单击宠物 → 她说一句话，**状态不变**（切状态挪到右键了）。
    // 台词按状态分组、轮流取，连点有变化，像在听她碎碎念。
    //
    // 台词从官方语音和剧情里挑，再按每个状态的情绪重写。她的性格是
    // **谦逊的强者 / 努力家 / 极度认真自律 / 耿直老实 / 骑士品德 / 怪力 / 猫奴 / 傲娇**：
    // 「你看起来很懈怠，不要因为周末就放松了自己」是官方舰桥语音，「非常、非常了不起」
    // 和「还不到我放弃的时候」都是剧情原话。
    //
    // ⚠️ **长度是硬约束**：气泡是单行 + CharacterEllipsis，缩到 0.6 倍时只放得下约 18 个字。
    // 加台词请控制在 **16 字以内**，否则小尺寸下会被截成省略号。
    static readonly Dictionary<string, string[]> StateLines = new Dictionary<string, string[]>
    {
        { "idle",  new string[] { "站直了。", "待机也是修行。", "别因为周末就松懈。", "要不要做几组挥枪练习？" } },
        { "think", new string[] { "让我想想。", "一条一条来。", "结果才是这个世界的语言。", "取舍，总是要做的。" } },
        { "work",  new string[] { "开始行动。", "体力活交给我。", "专心的时候别催我。", "认真起来就没那么快。" } },
        { "alert", new string[] { "这里需要你点个头。", "我不过是在贯彻我的正义。", "在等你，快一点。", "决定权在你。" } },
        { "done",  new string[] { "非常、非常了不起。", "完成了。下一个。", "收工。", "这就是所谓的成长吧。" } },
        { "awake", new string[] { "回来了，开始吧。", "别来无恙？", "是幽兰黛尔，也是卡斯兰娜。", "站在巨人的肩上。" } },
        { "sleep", new string[] { "……zzz……", "就一会儿……别吵……", "（抱紧了枕头）", "消灭崩坏之后再想生活。" } },
        { "error", new string[] { "还不到我放弃的时候。", "这次我来盯。", "是我的责任。", "重新来过。" } },
    };

    // 每个状态各自记一个游标 —— 用**同一个**游标会让"切到别的状态再切回来"也跳号，
    // 那样就没有"这个状态固定那几句轮着来"的感觉了。
    Dictionary<string, int> lineCursor = new Dictionary<string, int>();

    void SayLine()
    {
        string[] lines;
        if (!StateLines.TryGetValue(state, out lines) || lines.Length == 0) return;
        int i;
        if (!lineCursor.TryGetValue(state, out i)) i = 0;
        ShowLabel(lines[i], true);
        lineCursor[state] = (i + 1) % lines.Length;
    }

    // ================= 点它不抢焦点 =================
    // 项目最核心的承诺是"点它不会抢走你编辑器的焦点"。**光设 ShowActivated=false 是不够的** ——
    // WPF 那个属性只管第一次 Show() 时不激活。于是点击时 Windows 照样发 WM_MOUSEACTIVATE
    // 要求激活窗口，焦点就被抢走了（实测过：窗口样式里没有 NOACTIVATE，README 那句当时是假的）。
    //
    // 真正管用的是这个 **WS_EX_NOACTIVATE 样式**：它让窗口从根上无法被激活
    // —— 包括 WPF 自己那套激活逻辑。
    //
    // 踩过的两个坑（都实测过）：
    //  ①只加 WM_MOUSEACTIVATE 钩子（返回 MA_NOACTIVATE）**不够**：
    //    那个消息只拦"鼠标点击引发的激活"，而 WPF 处理鼠标输入时还会**自己主动激活窗口**，
    //    走的是另一条路，拦不到。实测：钩子确实返回了 3，但焦点照样被抢。
    //  ②这个样式会让窗口"永远不可能被激活"，而 WPF 的右键菜单是靠**宿主窗口失活**
    //    来自动关闭的 —— 于是菜单能弹出、点空白处却关不掉。
    //    解法不是去掉样式，而是在打开菜单时**显式拿鼠标捕获**（见 OnRightClick）。
    //
    // 钩子也留着：它顺手挡掉"点击时 Windows 要求激活"这条路，属于双保险。
    const int GWL_EXSTYLE = -20;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WM_MOUSEACTIVATE = 0x0021;
    const int MA_NOACTIVATE = 3;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    static void DontStealFocusOnClick(Window w)
    {
        try
        {
            IntPtr h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;

            int ex = GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);

            System.Windows.Interop.HwndSource src = System.Windows.Interop.HwndSource.FromHwnd(h);
            if (src != null) src.AddHook(NonActivatingHook);
        }
        catch (Exception)
        {
            // 拿不到就退回"至少 Show 时不激活"，不影响其它功能
        }
    }

    static IntPtr NonActivatingHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);   // 别激活我，把点击照常给我就行
        }
        return IntPtr.Zero;
    }

    // ---- 右键菜单期间：临时放开激活，关掉后收回并还焦点 ----
    // 菜单为什么需要这个：
    //   WS_EX_NOACTIVATE 让窗口**永远不会失活**，而 WPF 右键菜单关闭的默认逻辑
    //   靠的正是"宿主窗口失活" —— 于是"菜单能弹出、点空白处关不掉"。
    //   我在这个后果上绕了六版（采样鼠标、算菜单矩形、让菜单项认领点击…），
    //   每一版都冒出新的边界情况：矩形算成 1×1、"打开菜单那一次按键被当成点击"、
    //   按住菜单项超过宽限时间菜单就自己关了……全是和同一个根因较劲。
    //
    // 正确做法是**在菜单打开期间把那个样式临时撤掉**，让菜单的 popup 能正常激活/失活，
    // WPF 自己的关闭逻辑就工作了；关掉后再把样式装回去，并把焦点还给原来那个窗口。
    // 代价：菜单开着的时候焦点会短暂离开你的编辑器（用菜单本来就该这样），关掉就还回去。
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    IntPtr prevForeground = IntPtr.Zero;

    void MenuOpenedAllowActivate()
    {
        try
        {
            IntPtr h = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            if (h == IntPtr.Zero) return;
            prevForeground = GetForegroundWindow();          // 先记住"菜单之前是谁在前台"
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, ex & ~WS_EX_NOACTIVATE);
        }
        catch (Exception) { }
    }

    // ---- 菜单打开期间：铺一层"点击接收层"来接住落在菜单外面的点击 ----
    // 为什么需要它：WPF 的 ContextMenu 把"关闭"挂在**鼠标捕获**上
    // （社区里的原话：它捕获鼠标，控件又被写死成"鼠标一失去捕获就关"，算设计缺陷；
    //   建议改用原生 Popup 绕开）。在我们这种"窗口不激活"的场景里这套就是不可靠 ——
    // 这也是为什么"临时放开激活"能让菜单项好用，却依然点外面关不掉。
    //
    // 与其继续在它的内部机制里找办法，不如**让操作系统的命中测试去决定里外**：
    // 铺一层几乎全屏的接收层，落在菜单外面的点击都会打到它身上 → 关菜单。
    // 三个必须注意的点：
    //   ① 它得是"**几乎**透明"（alpha=1）而不是全透明 —— 全透明的分层窗口点击会穿透过去，接不到；
    //   ② 必须**明确排在菜单 popup 的后面**（SetWindowPos），否则它会把菜单自己的点击也吃掉，
    //      那正是"菜单项点不动"的成因；
    //   ③ 拿不到菜单 popup 的窗口句柄就**不铺**这一层 —— 宁可"点外面关不掉"，
    //      也不能因为判断不出里外而弄坏菜单本身。
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                    int X, int Y, int cx, int cy, uint flags);
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOACTIVATE = 0x0010;

    Window menuCatcher;   // 只创建一次、之后反复 Show/Hide（每次开关都新建一个全屏分层窗口太浪费）

    void ShowMenuCatcher(ContextMenu menu)
    {
        try
        {
            System.Windows.PresentationSource ps =
                System.Windows.PresentationSource.FromVisual(menu);
            System.Windows.Interop.HwndSource hs = ps as System.Windows.Interop.HwndSource;
            if (hs == null || hs.Handle == IntPtr.Zero) return;   // ③ 拿不到就不铺
            IntPtr menuHwnd = hs.Handle;

            if (menuCatcher == null)
            {
                Window w = new Window();
                w.WindowStyle = WindowStyle.None;
                w.AllowsTransparency = true;
                w.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));  // ① 几乎看不见但能被点到
                w.Topmost = true;
                w.ShowInTaskbar = false;
                w.ShowActivated = false;
                w.ResizeMode = ResizeMode.NoResize;
                w.Left = SystemParameters.VirtualScreenLeft;
                w.Top = SystemParameters.VirtualScreenTop;
                w.Width = SystemParameters.VirtualScreenWidth;
                w.Height = SystemParameters.VirtualScreenHeight;

                w.MouseDown += delegate(object s2, MouseButtonEventArgs e2)
                {
                    if (openMenu != null) openMenu.IsOpen = false;
                };

                w.Show();
                DontStealFocusOnClick(w);
                menuCatcher = w;
            }
            else
            {
                menuCatcher.Show();          // 复用：显示/隐藏，不新建
                // Show() 会重新应用窗口样式，可能把之前加的 WS_EX_NOACTIVATE 冲掉 ——
                // 那样接收层就能被激活、点它会抢走焦点（用户最在意的那条就破了）。所以每次都补一遍。
                // （加两次 hook 无害：同一个消息回同一个值。）
                DontStealFocusOnClick(menuCatcher);
            }

            // ② 每次都重排一次 z 序：菜单在上、接收层在下
            IntPtr catcherHwnd = new System.Windows.Interop.WindowInteropHelper(menuCatcher).Handle;
            SetWindowPos(catcherHwnd, menuHwnd, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch (Exception)
        {
            HideMenuCatcher();
        }
    }

    void HideMenuCatcher()
    {
        try
        {
            if (menuCatcher != null) menuCatcher.Hide();   // 只是藏起来，下次复用
        }
        catch (Exception) { }
    }

    void MenuClosedRestoreNoActivate()
    {
        try
        {
            IntPtr h = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            if (h != IntPtr.Zero)
            {
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);   // 装回去
            }
            if (prevForeground != IntPtr.Zero)
            {
                SetForegroundWindow(prevForeground);                   // 焦点还回去
                prevForeground = IntPtr.Zero;
            }
        }
        catch (Exception) { }
    }

    // ================= 全屏游戏时自动躲开 =================
    // 前台窗口铺满整块显示器 = 你在玩游戏（或看全屏视频）→ 把桌宠藏起来，退出全屏再放回来。
    //
    // 为什么值得做、而且**失败模式是安全的**：
    //   真·独占全屏（游戏接管显示输出）下 DWM 根本不会合成别的窗口，桌宠在那个模式下
    //   压根画不出来 —— 检测失败也无所谓。所以这个功能真正要解决的只有**无边框窗口化全屏**
    //   （Windows 仍当普通窗口，桌宠会盖在游戏上），而那恰恰是窗口矩形能精确测出来的模式。
    //
    // 坐标空间：全程只用 Win32（GetWindowRect 和 GetMonitorInfo 在同一个进程里同为物理像素），
    //   **绝不和 WPF 的 SystemParameters 混用** —— 那是 DIP 逻辑坐标。本机是 1920×1080 @125%，
    //   混用就是"最大化窗口被误判成全屏"那个经典 bug（dsh-pet 项目踩过）。
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    const int MONITOR_DEFAULTTONEAREST = 2;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_LAYERED    = 0x00080000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hWnd, int dwFlags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int nMaxCount);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr GetDesktopWindow();

    static readonly int MyPid = System.Diagnostics.Process.GetCurrentProcess().Id;

    // 「矩形正好铺满整屏」的窗口**不等于**"在玩游戏"。本机实测就有一堆这样的窗口，
    // 每一类都会让桌宠在你写字、切输入法的时候莫名消失：
    //   Progman/WorkerW/#32769 = 桌面本体（点一下桌面就中招）
    //   Shell_TrayWnd         = 任务栏
    //   CEF-OSC-WIDGET        = NVIDIA 覆盖层（常年 0,0-1536,864，且是 LAYERED）
    //   Windows.UI.Core.CoreWindow = 输入法宿主 TextInputHost
    static readonly string[] NotGameClasses = new string[] {
        "Progman", "WorkerW", "#32769", "SysShadow", "ForegroundStaging",
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "CEF-OSC-WIDGET", "Windows.UI.Core.CoreWindow"
    };

    // 前台窗口是不是"铺满整屏的游戏"。判不出来一律返回 false ——
    // 宁可多显示一只桌宠，也不能让它莫名消失（消失之后用户连右键都点不到，见 SummonSelf）。
    bool IsFullscreenForeground()
    {
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return false;
            if (h == GetDesktopWindow()) return false;

            // 自己的窗口不算：桌宠本体（NOACTIVATE，正常不会是前台）和菜单的点击接收层
            // —— 接收层是**整块虚拟屏幕大**的，认成"全屏游戏"就会自己把自己藏起来。
            int pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == MyPid) return false;

            if (!IsWindowVisible(h)) return false;

            StringBuilder sb = new StringBuilder(256);
            if (GetClassName(h, sb, sb.Capacity) == 0) return false;
            string cls = sb.ToString();
            foreach (string bad in NotGameClasses)
                if (cls == bad) return false;

            // 覆盖层/输入法这类"贴在最上面的辅助窗口"清一色带这几个扩展样式，一并挡掉。
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
            if ((ex & WS_EX_LAYERED) != 0) return false;
            if ((ex & WS_EX_NOACTIVATE) != 0) return false;

            RECT wr;
            if (!GetWindowRect(h, out wr)) return false;

            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST), ref mi)) return false;

            // 必须**精确相等**，不能用"包含"。
            // 实测：Win10 上最大化的普通窗口矩形是 (-7,-7,1543,831) —— 比显示器(0,0,1536,864)
            // 还大（最大化窗口的隐形边框会外扩），用"包含"会把每个最大化窗口都误判成全屏。
            // 而任务栏没隐藏时最大化的窗口右边/下边又小于显示器，精确相等同时也排除了它。
            return wr.Left == mi.rcMonitor.Left && wr.Top == mi.rcMonitor.Top
                && wr.Right == mi.rcMonitor.Right && wr.Bottom == mi.rcMonitor.Bottom;
        }
        catch (Exception)
        {
            return false;
        }
    }

    const int FullscreenDwellSamples = 2;   // 连续几次同向采样才动作（500ms 一次 → 约 1 秒）

    // 用轮询而不是 SetWinEventHook(EVENT_SYSTEM_FOREGROUND)：游戏常以**管理员权限**运行，
    // 非提权进程注册的前台事件钩子收不到它的通知，而 GetForegroundWindow 轮询不受影响。
    void CheckFullscreen()
    {
        // 用户手动藏到托盘了就别管了。这是这两套隐藏逻辑**唯一的交叉点** ——
        // 少了这一句，切出全屏时 ShowPetAfterFullscreen() 会把用户刚藏好的宠物弹回来。
        if (hiddenByUser) return;

        if (!fullscreenAutoHide)
        {
            // 用户可能在"已经被藏起来"的时候把开关关掉，这里兜一手
            if (petHidden) ShowPetAfterFullscreen();
            return;
        }

        bool isFullscreen = IsFullscreenForeground();

        if (isFullscreen == petHidden) { fullscreenStreak = 0; return; }  // 现状已经是想要的，不用动

        fullscreenStreak++;
        if (fullscreenStreak < FullscreenDwellSamples) return;  // 防抖：alt-tab 中途别闪
        fullscreenStreak = 0;

        if (isFullscreen) HidePetForFullscreen();
        else ShowPetAfterFullscreen();
    }

    void HidePetForFullscreen()
    {
        if (petHidden) return;
        petHidden = true;
        try
        {
            // 先收菜单：菜单的 popup 不跟着宿主窗口隐藏，直接藏主窗口会留下一个孤儿菜单
            // （接收层也还铺着）。这样关菜单顺带把接收层收掉、焦点还回去。
            if (openMenu != null && openMenu.IsOpen) openMenu.IsOpen = false;

            win.Hide();
            animTimer.Stop();   // 玩游戏时省点 CPU；tickTimer 照常跑，状态继续更新
        }
        catch (Exception) { }
    }

    void ShowPetAfterFullscreen()
    {
        if (!petHidden) return;
        petHidden = false;
        try
        {
            win.Show();
            // 每次 Show() 都会重新应用窗口样式，把 WS_EX_NOACTIVATE 冲掉 ——
            // 不补这一下，从游戏回来之后点桌宠就会抢走你编辑器的焦点。
            // 同一个坑在 menuCatcher 复用分支上已经踩过一次，顺序照抄那边。
            DontStealFocusOnClick(win);

            // Hide 的时候停了动画，这里按当前状态重新开起来
            // （不能靠 SetState(state) —— 它遇到"同一个循环状态"会早退，起不到重启作用）
            Anim a;
            if (anims.TryGetValue(state, out a) && a.Loop) animTimer.Start();
        }
        catch (Exception) { }
    }

    // ================= 隐藏状态：藏到系统托盘待命 =================
    // 这是**用户主动**的隐藏，和上面的「全屏游戏自动隐藏」是两回事（所以是两个字段）。
    //
    // 语义（用户确认的"真待命"）：**隐藏期间窗口绝不自己弹出来**。
    // hook 事件照常更新内部状态（所以回来时姿势是对的、托盘提示也是最新的），
    // 但窗口不现形 —— "隐藏"得是可靠的，否则刚藏好又被一个事件弹回来，等于没藏。
    void HideToTray()
    {
        if (hiddenByUser) return;
        hiddenByUser = true;
        try
        {
            // 和全屏隐藏同款：先收菜单，否则它的 popup 不跟着宿主窗口隐藏，
            // 直接藏主窗口会留下一个孤儿菜单（接收层也还铺着）。
            if (openMenu != null && openMenu.IsOpen) openMenu.IsOpen = false;

            win.Hide();
            animTimer.Stop();       // 看不见就别画；tickTimer 照常跑，状态继续更新
            ShowTrayIcon();
        }
        catch (Exception) { }
    }

    void ShowFromTray()
    {
        if (!hiddenByUser) return;
        hiddenByUser = false;
        try
        {
            HideTrayIcon();
            win.Show();
            // 每次 Show() 都会重新应用窗口样式，把 WS_EX_NOACTIVATE 冲掉 ——
            // 不补这一下，从托盘回来之后点桌宠就会抢走你编辑器的焦点。（和全屏那套同一个坑）
            DontStealFocusOnClick(win);

            // Hide 时停了动画，这里按当前状态重新开起来。
            // 不能靠 SetState(state)：它遇到"同一个循环状态"会早退，起不到重启作用。
            Anim a;
            if (anims.TryGetValue(state, out a) && a.Loop) animTimer.Start();

            SayLine();      // 回来说一句（状态没变，就是冒个泡）
        }
        catch (Exception) { }
    }

    // ---- 托盘图标 ----
    // **不新增 .ico 文件**：从素材里现场生成 —— 取 idle 那张图的上半部分（头部）缩到 32x32。
    // 用 System.Drawing 是因为 NotifyIcon 要的就是那种 Icon。
    // csc.rsp **默认已经引用了** System.Windows.Forms 和 System.Drawing（见 build.sh 的注释），
    // 所以**别再显式 /r: 一次**，会报 CS1703 重复程序集。
    // 这里所有 System.Drawing / .Windows.Forms 的类型都**写全名**，不加 using：
    // 那两个命名空间和 System.Windows.Media 撞了一堆名字（Color/Brush/Point/Size/Application…）。
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    void ShowTrayIcon()
    {
        if (trayIcon == null)
        {
            trayIcon = new System.Windows.Forms.NotifyIcon();
            trayIcon.Icon = MakeTrayIcon();
            trayIcon.Text = "ClaudePet";
            trayIcon.Visible = false;
            trayIcon.DoubleClick += delegate(object s, EventArgs e) { ShowFromTray(); };
            trayIcon.ContextMenuStrip = MakeTrayMenu();
        }
        UpdateTrayText();
        trayIcon.Visible = true;    // Visible 默认 false，不设它图标压根不显示
    }

    void HideTrayIcon()
    {
        if (trayIcon != null) trayIcon.Visible = false;
    }

    // 退出前必须收掉，否则托盘里会留一个**幽灵图标**（进程没了，图标还在，
    // 要鼠标划过去才消失）。这是 NotifyIcon 有名的坑。
    void DisposeTray()
    {
        if (trayIcon == null) return;
        try { trayIcon.Visible = false; trayIcon.Dispose(); }
        catch (Exception) { }
        trayIcon = null;
    }

    // 托盘提示文字实时反映当前状态 —— "待命"期间鼠标一悬停就知道 Claude 在干嘛。
    // ⚠️ NotifyIcon.Text 在 .NET Framework 下**上限 63 字符**，超了抛 ArgumentException。
    void UpdateTrayText()
    {
        if (trayIcon == null) return;
        string t = "ClaudePet · " + StateLabel(state);
        if (t.Length > 63) t = t.Substring(0, 63);
        try { trayIcon.Text = t; }
        catch (Exception) { }
    }

    // 头部位置**是量出来的，不是写死的比例**。
    // 一开始我用比例估（"取上中部那一块"），结果裁出来只有一撮刘海、连眼睛都没进来 ——
    // 缩到 32x32 就是一团黄色。原因是我在缩略图上目测的坐标一直偏。
    // 现在改成：先在上半图里找**虹膜那种饱和蓝**，拿到"两只眼合起来"的包围盒，
    // 再以它为中心取一个正方形。这个办法不依赖任何写死的数字，重抠一次素材也不会失准。
    //   实测 akimbo.png(1487x1991)：虹膜 bbox = (306,782)-(709,965)，眼中心 (507,873)
    System.Drawing.Icon MakeTrayIcon()
    {
        try
        {
            string src = Path.Combine(rootDir, "assets", "cut", "akimbo.png");
            if (!File.Exists(src)) return System.Drawing.SystemIcons.Application;
            using (System.Drawing.Bitmap bm = (System.Drawing.Bitmap)System.Drawing.Image.FromFile(src))
            {
                int sx, sy, side;
                FindHeadBox(bm, out sx, out sy, out side);
                using (System.Drawing.Bitmap ic = new System.Drawing.Bitmap(32, 32))
                {
                    using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(ic))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(bm, new System.Drawing.Rectangle(0, 0, 32, 32),
                                        new System.Drawing.Rectangle(sx, sy, side, side),
                                        System.Drawing.GraphicsUnit.Pixel);
                    }
                    IntPtr h = ic.GetHicon();
                    try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(h).Clone(); }
                    finally { DestroyIcon(h); }   // 不销毁会漏 GDI 句柄
                }
            }
        }
        catch (Exception)
        {
            // 取素材失败也必须有个图标 —— 没图标的话托盘里什么都不显示，用户会以为"隐藏功能坏了"
            return System.Drawing.SystemIcons.Application;
        }
    }

    // 算出"头部"要裁的那块正方形（越界会被夹回图内）。
    static void FindHeadBox(System.Drawing.Bitmap bm, out int sx, out int sy, out int side)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        int step = Math.Max(1, bm.Width / 250);          // 抽样扫就够定包围盒，不必逐像素
        for (int y = 0; y < bm.Height / 2; y += step)    // **只看上半**：下半那条蓝裙子也满足"饱和蓝"
        {
            for (int x = 0; x < bm.Width; x += step)
            {
                System.Drawing.Color c = bm.GetPixel(x, y);
                if (c.A < 8) continue;
                if (c.B > 140 && c.B - c.R > 90 && c.B - c.G > 40 && c.G > 60)
                {
                    if (x < x0) x0 = x; if (y < y0) y0 = y;
                    if (x > x1) x1 = x; if (y > y1) y1 = y;
                }
            }
        }

        if (x1 > x0 && (x1 - x0) > bm.Width / 20)        // 找到了虹膜（太小的不算）
        {
            double eyeW = x1 - x0 + 1, eyeH = y1 - y0 + 1;
            double cx = (x0 + x1) / 2.0;
            // 上移一点：脸比眼睛高，得把额头和头发让进来
            double cy = (y0 + y1) / 2.0 - eyeH * 0.35;
            // 两只眼合起来 ≈ 脸宽的 0.57、头高的 0.29 —— 反推边长取大的那个
            side = (int)Math.Round(Math.Max(eyeW * 1.75, eyeH * 3.4));
            sx = (int)Math.Round(cx - side / 2.0);
            sy = (int)Math.Round(cy - side / 2.0);
        }
        else
        {
            // 兜底：一组按 akimbo 量的固定比例（脸在图的左中上部）。
            // 走到这里说明素材换得比较离谱 —— 图标不完美，但至少不是空的。
            side = (int)(bm.Width * 0.48);
            sx = (int)(bm.Width * 0.10);
            sy = (int)(bm.Height * 0.226);
        }

        if (side > bm.Width) side = bm.Width;
        if (side > bm.Height) side = bm.Height;
        if (sx < 0) sx = 0;
        if (sy < 0) sy = 0;
        if (sx + side > bm.Width) sx = bm.Width - side;
        if (sy + side > bm.Height) sy = bm.Height - side;
    }

    System.Windows.Forms.ContextMenuStrip MakeTrayMenu()
    {
        System.Windows.Forms.ContextMenuStrip m = new System.Windows.Forms.ContextMenuStrip();
        // 藏起来之后右键是点不到宠物的，托盘菜单是**唯一**的出口，所以「退出」必须在这儿有一份
        System.Windows.Forms.ToolStripMenuItem show = new System.Windows.Forms.ToolStripMenuItem("显示桌宠");
        show.Click += delegate(object s, EventArgs e) { ShowFromTray(); };
        System.Windows.Forms.ToolStripMenuItem quit = new System.Windows.Forms.ToolStripMenuItem("退出");
        quit.Click += delegate(object s, EventArgs e) { QuitApp(); };
        m.Items.Add(show);
        m.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        m.Items.Add(quit);
        return m;
    }

    // ================= 开机自启 =================
    // 读写 HKCU\...\Run 里的一项。和 install-autostart.ps1 是**同一个开关**
    // （两边动的是同一个值），所以菜单和脚本永远不会不同步。
    //
    // 为什么用注册表键而不是"启动"文件夹：那个文件夹在 C 盘，而这个项目的原则是 C 盘零占用。
    // 只动 HKCU，不需要管理员权限。
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "ClaudePet";

    static bool IsAutostartOn()
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k =
                   Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (k == null) return false;
                return k.GetValue(RunValueName) != null;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    // 返回 true = 写成功了（失败时菜单项会退回真实状态，不会骗人）
    static bool SetAutostart(bool on)
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k =
                   Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (k == null) return false;
                if (on)
                {
                    string exe = Assembly.GetExecutingAssembly().Location;
                    k.SetValue(RunValueName, "\"" + exe + "\"", Microsoft.Win32.RegistryValueKind.String);
                }
                else
                {
                    k.DeleteValue(RunValueName, false);   // 不存在也不报错
                }
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // 状态名的中文说法，只用在气泡里（单击切状态时显示，让人知道现在演的是哪个）
    static string StateLabel(string s)
    {
        switch (s)
        {
            case "idle":  return "待机";
            case "think": return "在想事情";
            case "work":  return "正在干活";
            case "alert": return "等你确认";
            case "done":  return "干完了";
            case "awake": return "回来了";
            case "sleep": return "睡着了";
            case "error": return "出错了";
            case "hidden": return "已隐藏";   // 托盘提示用得到；它没有素材，不在 CycleOrder 里
        }
        return s;
    }

    // ================= 右键：单击切状态 / 双击弹菜单 =================
    // 2026-09-12 改的语义：**单击 = 切状态**（原来在左键），**双击 = 弹菜单**（原来在单击）。
    // 左键腾出来给"说话"（见 SayLine）—— 左键是最高频的按键，值得留给不改变状态的动作。
    //
    // 代价绕不开：系统必须先等一个"双击间隔"才能确定这是单击、而不是双击的前半截。
    // 所以右键切状态有 RightDblClickMs 的迟滞（用户确认接受）。
    // 判定就是"等第二下"：第一下起表，表到点 = 单击；表没到点又来了第二下 = 双击。
    // ⚠️ 这个定时器**必须独立于 tickTimer**(500ms)，它俩节奏不一样，共用会互相拖。
    const int RightDblClickMs = 350;
    DispatcherTimer rightClickTimer;

    void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        // 菜单开着的时候再按右键 = 收起来。保持原有行为，也不参与单/双击判定 ——
        // 否则"关菜单"之后的半秒里再点一下会变成一个莫名其妙的切状态。
        if (openMenu != null && openMenu.IsOpen)
        {
            rightClickTimer.Stop();
            openMenu.IsOpen = false;
            return;
        }

        if (rightClickTimer.IsEnabled)
        {
            rightClickTimer.Stop();     // 第二下来了 → 双击 → 弹菜单
            OpenPetMenu();
            return;
        }

        rightClickTimer.Start();        // 第一下：先等一下，看有没有第二下
    }

    // ================= 右键菜单 =================
    // 这个函数只管**开关菜单和收尾**（那套 Open/Closed + 接收层 + 临时放开 NOACTIVATE 的
    // 机制是七版才调通的，别动）。菜单内容在下面那组 Mk* 函数里，一项一个。
    //
    // 拆开纯粹是因为它原来是 159 行、8 个菜单项的委托全挤在里面，改某一项得先找它在哪。
    // 注意：下面的拆分是**纯搬位置** —— 逻辑、顺序、连"菜单已经开着时先建再丢掉"这个
    // 看起来浪费但无害的原有行为，都原样保留。
    void OpenPetMenu()
    {
        ContextMenu menu = BuildMenu();
        menu.PlacementTarget = win;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        // 菜单已经开着的时候再按右键 = 收起来（切换），而不是"关掉旧的、在鼠标新位置开一个"。
        // 后者会闪一下，看起来像"菜单被重启了"。
        if (openMenu != null && openMenu.IsOpen)
        {
            openMenu.IsOpen = false;
            return;
        }

        menu.IsOpen = true;
        openMenu = menu;
        menu.Closed += delegate(object s2, RoutedEventArgs e2)
        {
            if (openMenu == menu) openMenu = null;
            HideMenuCatcher();
            MenuClosedRestoreNoActivate();
        };
        MenuOpenedAllowActivate();

        // 接收层要等菜单的 popup 真正建出来（才有窗口句柄可排 z 序），所以排到这一轮之后再做。
        // 顺便也避开了"打开菜单的那一次点击"打到接收层上把自己关掉。
        win.Dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(delegate() { ShowMenuCatcher(menu); }));
    }

    // 建出完整菜单。这里的顺序 = 菜单里显示的顺序。
    ContextMenu BuildMenu()
    {
        ContextMenu menu = new ContextMenu();
        menu.FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        menu.FontSize = 12;

        menu.Items.Add(MkScaleMenu());
        menu.Items.Add(MkOpacityMenu());
        menu.Items.Add(new Separator());
        menu.Items.Add(MkResetItem());
        menu.Items.Add(MkReloadItem());
        menu.Items.Add(MkTestMenu());
        menu.Items.Add(new Separator());
        menu.Items.Add(MkAutostartItem());
        menu.Items.Add(MkFullscreenItem());
        menu.Items.Add(new Separator());
        menu.Items.Add(MkHideItem());
        menu.Items.Add(MkQuitItem());
        return menu;
    }

    // 「隐藏到托盘」。
    // 注意它**不放进 CycleOrder**、也不进「测试各状态」子菜单 —— 右键单击是切状态，
    // 要是 hidden 在循环里，连点几下就把宠物藏没了，找回来还得靠托盘。
    // 隐藏只能从这一项进，这是个"明确的动作"。
    MenuItem MkHideItem()
    {
        MenuItem m = new MenuItem();
        m.Header = "隐藏到托盘";
        m.Click += delegate(object s2, RoutedEventArgs e2) { HideToTray(); };
        return m;
    }

    MenuItem MkScaleMenu()
    {
        MenuItem mScale = new MenuItem();
        mScale.Header = "大小";
        double[] scales = new double[] { 0.5, 0.75, 1.0, 1.5, 2.0 };
        string[] scaleNames = new string[] { "50%", "75%", "100%", "150%", "200%" };
        for (int i = 0; i < scales.Length; i++)
        {
            double s = scales[i];
            MenuItem it = new MenuItem();
            it.Header = scaleNames[i];
            it.IsCheckable = true;
            it.IsChecked = Math.Abs(scale - s) < 0.01;
            it.Click += delegate(object s2, RoutedEventArgs e2) { ApplyScale(s); SaveConfig(); };
            mScale.Items.Add(it);
        }
        return mScale;
    }

    MenuItem MkOpacityMenu()
    {
        MenuItem mOpa = new MenuItem();
        mOpa.Header = "透明度";
        double[] opas = new double[] { 1.0, 0.92, 0.75, 0.5, 0.3 };
        string[] opaNames = new string[] { "100%", "92%", "75%", "50%", "30%" };
        for (int i = 0; i < opas.Length; i++)
        {
            double o = opas[i];
            MenuItem it = new MenuItem();
            it.Header = opaNames[i];
            it.IsCheckable = true;
            it.IsChecked = Math.Abs(opacity - o) < 0.01;
            it.Click += delegate(object s2, RoutedEventArgs e2) { opacity = o; win.Opacity = o; SaveConfig(); };
            mOpa.Items.Add(it);
        }
        return mOpa;
    }

    MenuItem MkResetItem()
    {
        MenuItem mReset = new MenuItem();
        mReset.Header = "回到右下角";
        mReset.Click += delegate(object s2, RoutedEventArgs e2)
        {
            posX = double.NaN; posY = double.NaN;
            win.Left = SystemParameters.WorkArea.Right - win.Width - CornerMargin;
            win.Top = SystemParameters.WorkArea.Bottom - win.Height - CornerMargin;
            SaveConfig();
        };
        return mReset;
    }

    MenuItem MkReloadItem()
    {
        MenuItem mReload = new MenuItem();
        mReload.Header = "重新载入素材";
        mReload.Click += delegate(object s2, RoutedEventArgs e2)
        {
            try { LoadSprites(); SetState("idle"); ShowLabel("素材已重新载入"); }
            catch (Exception ex) { MessageBox.Show("载入失败：" + ex.Message, "Claude Pet"); }
        };
        return mReload;
    }

    MenuItem MkTestMenu()
    {
        MenuItem mTest = new MenuItem();
        mTest.Header = "测试各状态";
        // 顺序跟着 CycleOrder 走（不是按中文名字顺），这样两个入口看到的状态顺序是一致的。
        // awake 曾经漏在这儿：它在 CycleOrder 和 StateLabel 里都有，所以只能靠单击循环切到，
        // 没法从这里直接预览 —— 而它是有专属姿势的（clasp 双手握拳）。
        MenuItem[] testItems = new MenuItem[] {
            MkTest("idle (待机)"), MkTest("think (思考)"), MkTest("work (工作)"),
            MkTest("alert (等你确认)"), MkTest("done (完成)"), MkTest("awake (回来了)"),
            MkTest("sleep (睡觉)"), MkTest("error (出错)")
        };
        foreach (MenuItem ti in testItems) mTest.Items.Add(ti);
        return mTest;
    }

    MenuItem MkAutostartItem()
    {
        MenuItem mAuto = new MenuItem();
        mAuto.Header = "开机自启";
        mAuto.IsCheckable = true;
        mAuto.IsChecked = IsAutostartOn();
        mAuto.Click += delegate(object s2, RoutedEventArgs e2)
        {
            // 不依赖 IsCheckable 自己翻转的结果：直接读**真实状态**再取反，
            // 这样不管菜单项当前显示成什么，点下去的结果都是确定的。
            bool want = !IsAutostartOn();
            if (SetAutostart(want))
            {
                mAuto.IsChecked = want;
                ShowLabel(want ? "已开启开机自启" : "已取消开机自启");
            }
            else
            {
                mAuto.IsChecked = IsAutostartOn();
                ShowLabel("自启设置失败：注册表写不进去");
            }
        };
        return mAuto;
    }

    MenuItem MkFullscreenItem()
    {
        MenuItem mFs = new MenuItem();
        mFs.Header = "全屏游戏时自动隐藏";
        mFs.IsCheckable = true;
        mFs.IsChecked = fullscreenAutoHide;
        mFs.Click += delegate(object s2, RoutedEventArgs e2)
        {
            // 和「开机自启」一样：不依赖 IsCheckable 自己翻转的结果，直接按真实值取反，
            // 这样不管菜单项当前显示成什么，点下去的结果都是确定的。
            fullscreenAutoHide = !fullscreenAutoHide;
            fullscreenStreak = 0;
            mFs.IsChecked = fullscreenAutoHide;
            SaveConfig();

            if (fullscreenAutoHide)
            {
                ShowLabel("全屏游戏时会自动躲开");
                // 现在就全屏的话立刻生效，不用等定时器；里面会先收掉菜单再藏窗口
                if (IsFullscreenForeground()) HidePetForFullscreen();
            }
            else
            {
                if (petHidden) ShowPetAfterFullscreen();
                ShowLabel("已关闭全屏自动隐藏");
            }
        };
        return mFs;
    }

    MenuItem MkQuitItem()
    {
        MenuItem mQuit = new MenuItem();
        mQuit.Header = "退出";
        mQuit.Click += delegate(object s2, RoutedEventArgs e2) { QuitApp(); };
        return mQuit;
    }

    // 退出。托盘菜单的「退出」也走这里 —— 藏起来之后右键点不到宠物，
    // 那条路径是用户唯一的出口，两边必须是同一套收尾。
    void QuitApp()
    {
        SaveConfig();
        DisposeTray();      // 不收掉会在托盘里留一个幽灵图标
        Application.Current.Shutdown();
    }


    MenuItem MkTest(string stateName)
    {
        MenuItem it = new MenuItem();
        it.Header = stateName;
        string st = stateName.Split(' ')[0];
        it.Click += delegate(object s2, RoutedEventArgs e2) { SetState(st); };
        return it;
    }

    // ================= 配置 =================
    void LoadConfig()
    {
        try
        {
            if (!File.Exists(configPath)) return;
            JavaScriptSerializer ser = new JavaScriptSerializer();
            Dictionary<string, object> c =
                ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath, Encoding.UTF8));

            // 配置里的数字要**逐个校验范围**，不能拿到就直接用：
            //   * 端口非法 → new UdpClient(port) 在监听线程里抛异常被吞掉 → 桌宠看着一切正常，
            //     但一个事件都收不到、也不告诉任何人为什么（最容易误判成"hook 坏了"）。
            //   * 透明度越界 → win.Opacity 赋值时 WPF 直接抛，桌宠连启动都启动不了。
            if (c.ContainsKey("port"))
            {
                int p = ToInt(c["port"], port);
                if (p > 0 && p < 65536) port = p;
            }
            if (c.ContainsKey("scale"))
            {
                double sc = ToDouble(c["scale"], scale);
                if (sc >= 0.3 && sc <= 3.0) scale = sc;
            }
            if (c.ContainsKey("opacity"))
            {
                double op = ToDouble(c["opacity"], opacity);
                if (op >= 0.1 && op <= 1.0) opacity = op;
            }
            if (c.ContainsKey("x") && c["x"] != null) posX = ToDouble(c["x"], double.NaN);
            if (c.ContainsKey("y") && c["y"] != null) posY = ToDouble(c["y"], double.NaN);
            if (c.ContainsKey("fullscreenAutoHide"))
                fullscreenAutoHide = ToBool(c["fullscreenAutoHide"], fullscreenAutoHide);
        }
        catch (Exception)
        {
            // 配置坏了就用默认值，不打扰用户
        }
    }

    void SaveConfig()
    {
        try
        {
            if (win != null)
            {
                posX = win.Left;
                posY = win.Top;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"port\": ").Append(port).Append(",\n");
            sb.Append("  \"scale\": ").Append(scale.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"opacity\": ").Append(opacity.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"x\": ").Append(Num(posX)).Append(",\n");
            sb.Append("  \"y\": ").Append(Num(posY)).Append(",\n");
            sb.Append("  \"fullscreenAutoHide\": ").Append(fullscreenAutoHide ? "true" : "false").Append("\n");
            sb.Append("}\n");
            File.WriteAllText(configPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception)
        {
        }
    }

    // 从 config.json 里读端口。独立于 Pet 实例，因为"召唤"发生在 LoadConfig 之前。
    // 只认第一个 "port": 数字，够用；读不到或数字不合法就退回默认端口。
    static int ResolvePortFromConfig()
    {
        try
        {
            string path = Path.Combine(FindRoot(), "config.json");
            if (!File.Exists(path)) return DefaultPort;
            string txt = File.ReadAllText(path, Encoding.UTF8);

            int i = txt.IndexOf("\"port\"", StringComparison.Ordinal);
            if (i < 0) return DefaultPort;
            i = txt.IndexOf(':', i);
            if (i < 0) return DefaultPort;

            int j = i + 1;
            while (j < txt.Length && (txt[j] == ' ' || txt[j] == '\t')) j++;
            int k = j;
            while (k < txt.Length && char.IsDigit(txt[k])) k++;
            if (k == j) return DefaultPort;

            int p = int.Parse(txt.Substring(j, k - j), CultureInfo.InvariantCulture);
            return (p > 0 && p < 65536) ? p : DefaultPort;
        }
        catch (Exception)
        {
            return DefaultPort;
        }
    }

    static string Num(double d)
    {
        if (double.IsNaN(d)) return "null";
        return Math.Round(d).ToString(CultureInfo.InvariantCulture);
    }

    // 给用户看一条提示。**不要用 MessageBox**：这台机器上模态框会在 2~3 秒内
    // 被自动关掉（实测连新鲜编译的空白 MessageBox 也一样），用户根本来不及看，
    // 看起来就跟没弹一样。所以自己画一张无按钮的小卡片，8 秒后自己消失——
    // 没有"确定"可点，也就没什么能把它提前关掉。
    static void ShowNotice(string text)
    {
        try
        {
            Window w = new Window();
            w.WindowStyle = WindowStyle.None;
            w.AllowsTransparency = true;
            w.Background = Brushes.Transparent;
            w.Topmost = true;
            w.ShowInTaskbar = false;
            w.ShowActivated = false;
            w.ResizeMode = ResizeMode.NoResize;
            w.SizeToContent = SizeToContent.WidthAndHeight;

            Border box = new Border();
            box.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
            box.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x5C, 0x94));
            box.BorderThickness = new Thickness(1.4);
            box.CornerRadius = new CornerRadius(9);
            box.Padding = new Thickness(12, 8, 12, 8);
            box.MaxWidth = 380;

            TextBlock tb = new TextBlock();
            tb.Text = text;
            tb.TextWrapping = TextWrapping.Wrap;
            tb.FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            tb.FontSize = 12.5;
            tb.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x33, 0x44));
            box.Child = tb;
            w.Content = box;

            w.Show();
            DontStealFocusOnClick(w);   // 提示卡片也一样：不该为了一条提示把焦点抢走
            // SizeToContent 要等 Show 之后才有真实尺寸，所以位置在这里算
            w.Left = SystemParameters.WorkArea.Right - w.ActualWidth - CornerMargin;
            w.Top = SystemParameters.WorkArea.Bottom - w.ActualHeight - CornerMargin;

            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromSeconds(8);
            t.Tick += delegate(object s, EventArgs e)
            {
                t.Stop();
                w.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            };
            t.Start();

            Dispatcher.Run();       // 这个实例本来就是一次性的，显示完就退出
        }
        catch (Exception)
        {
            // WPF 起不来就退回老办法，总比什么都不说强
            try
            {
                MessageBox.Show(text, "Claude Pet", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception) { }
        }
    }

    // 给已经在跑的那只发一条消息。waitAckMs > 0 时等它回一个 ack，
    // 返回是否收到——收不到就说明对面占着单实例锁却没在监听。
    // 端口：这里跑在 LoadConfig 之前，用不了 port 字段，所以是现读 config.json
    // （以前这里写着"端口写死 DefaultPort"，那是修复前的旧注释，和函数内的实现相反）。
    static bool SendRaw(string payload, int waitAckMs)
    {
        UdpClient u = null;
        try
        {
            byte[] b = Encoding.UTF8.GetBytes(payload);
            u = new UdpClient();
            // 端口要和**正在跑的那只**用的一致：它在 config.json 里，所以这里先读一眼。
            // （以前写死 47821：万一改过端口，第二只就会去敲一个没人听的端口、
            //   然后弹一句"已经有一只桌宠在运行，但它没有回应"——其实那只活得好好的。）
            u.Send(b, b.Length, "127.0.0.1", ResolvePortFromConfig());

            if (waitAckMs <= 0) return true;

            u.Client.ReceiveTimeout = waitAckMs;
            IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
            u.Receive(ref from);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (u != null) { try { u.Close(); } catch (Exception) { } }
        }
    }
}
