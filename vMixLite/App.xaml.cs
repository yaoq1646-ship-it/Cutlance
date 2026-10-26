using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Cutlance
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // —— 改名搬运（vMixLite → Cutlance）——
            // 必须排在 ErrorLog 初始化之前：老板升级后第一次启动，配置/最近工程/日志
            // 都要先原样搬过来，否则会表现成「设置全丢了」。只搬「旧有、新无」，旧目录不删。
            Services.LegacyMigration.Run();

            // —— 全局异常兜底：把"闪退"变成可读的报错框 + 落盘日志，而不是直接退出 ——
            // ⚠ 这里是**唯一**的注册点。MainWindow 曾经也注册过一份同款处理器，
            //   结果是同一个异常被弹两次框（App 一个、MainWindow 一个），已删掉那边那份。
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            // —— 错误保存目录（老板 2026-10-20）——
            // 一启动就把「这台机器长什么样」写进 logs：版本、系统、位数、.NET、CPU、内存、屏幕。
            // 装到别人电脑上出问题时，光这一个文件就能判断是不是环境差异（无 N 卡 / 没装 NDI /
            // 屏幕缩放 4K / 缺 .NET 运行时），不必靠猜。
            Services.ErrorLog.Info("程序启动");
            Exit += (_, _) => Services.ErrorLog.Info("程序退出");

            // 关「主窗口」即整程序退出（默认是"最后一个窗口关闭才退出"，
            // 若此时「输出设置/音频混音台」还开着，进程会残留、声音继续放 —— 必须显式指定）
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            // —— 原生标题栏跟随深色主题 ——
            // 挂在 Window 基类上：任何窗口（含自定义派生窗）Loaded 时都自动生效，
            // 不必在每个窗口的 XAML/构造里各写一次。
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, _) => Services.DarkTitleBar.Apply(s as Window)));

            // 显式创建并显示主窗口（避免 StartupUri 解析差异）
            // ⚠ 启动阶段的异常（XAML 解析 / 字体枚举 / 资源加载）走不到 DispatcherUnhandledException
            //   —— 那时消息循环还没跑起来。必须在这里兜一层落盘，
            //   否则表现就是「双击后进程直接没了、日志里什么都没有」。
            // 命令行参数支持：Cutlance.exe "D:\...\某工程.cutlance" → 启动即载入该工程。
            // （把 .cutlance 文件拖到 exe 上、或建快捷方式带参数，都能一键回到上次的工作状态）
            // 旧后缀 .vmlite 一并认 —— 改名不能把老板手里的老工程变成打不开的文件。
            var projArg = e.Args?.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a) &&
                (a.EndsWith(".cutlance", StringComparison.OrdinalIgnoreCase) ||
                 a.EndsWith(".vmlite", StringComparison.OrdinalIgnoreCase)));

            // ⚠ 必须在 new MainWindow() **之前**把工程里的输出分辨率读出来。
            //   合成器（MonitorBridge）在主窗构造时就按 StreamService.OutputWidth/Height
            //   一次性分配好画面缓冲、预览位图、离屏渲染画布，之后再改数字它也不会重建。
            //   以前这里把载入工程挂在 MainWindow.Loaded 上 → 竖屏工程（1080×1920）永远
            //   按默认 16:9 建合成器，实际出图是横屏，而且点「开始输出」还会被静默改回去。
            if (!string.IsNullOrEmpty(projArg)) PresetOutputSizeFromProject(projArg);

            MainWindow main;
            try
            {
                main = new MainWindow();
            }
            catch (Exception ex)
            {
                LogFatal("主窗口构造失败", ex);
                throw;
            }

            if (!string.IsNullOrEmpty(projArg))
                main.Loaded += (_, _) => main.LoadProjectFrom(projArg);

            // ⚠ 自动化测试专用开关（2026-10-18 加）：
            //   老板投诉「跑测试时他的界面不断闪白、正在输入的文字没了」——
            //   除了脚本抢前台，**主窗自己 Show() 也会抢**：WPF 默认显示即激活，
            //   于是每起一次被测程序，老板的输入焦点就被夺走一次。
            //   加 --noactivate 后窗口照常显示、照常渲染，但**不夺取前台焦点**。
            //   正常双击启动不受影响（默认仍然激活）。
            bool noActivate = e.Args?.Any(a =>
                string.Equals(a, "--noactivate", StringComparison.OrdinalIgnoreCase)) == true;
            if (noActivate) main.ShowActivated = false;

            // --offscreen：把窗口整体挪到桌面外（配合 PrintWindow 截图做回归测试）。
            // 用途：老板正在用电脑时，我可以在**完全不打扰他**的前提下验证界面渲染 —— 窗口
            // 既不出现在屏幕上、也不抢焦点，只是被系统渲染到内存里再截图。
            // 必须在 Show() 之前设 —— 构造里的 FitToDesktop 会把它摆回桌面。
            bool offscreen = e.Args?.Any(a =>
                string.Equals(a, "--offscreen", StringComparison.OrdinalIgnoreCase)) == true;
            if (offscreen)
            {
                main.ShowActivated = false;
                main.Left = -12000;
                main.Top = -12000;
            }

            // --geotest：进程内自检（精调面板 / 组合预设 / 吸附 / 推输出），见 MainWindow.RunGeoTest。
            // 必须排在工程载入之后 —— 自检要有输入源才跑得起来。
            if (e.Args?.Any(a => string.Equals(a, "--geotest", StringComparison.OrdinalIgnoreCase)) == true)
                main.Loaded += (_, _) => main.RunGeoTest();

            // --synth：启动后直接打开「监视窗 + 切换条」（P4 内存合成切换台）。
            // 给自动化验证用，也给老板一个可以固定到任务栏快捷方式的用法：
            //   Cutlance.exe "某工程.cutlance" --synth → 开机就是导播台。
            bool openSynth = e.Args?.Any(a =>
                string.Equals(a, "--synth", StringComparison.OrdinalIgnoreCase)) == true;
            if (openSynth)
                main.Loaded += (_, _) => main.OpenSynthWindow();

            try { main.Show(); }
            catch (Exception ex)
            {
                LogFatal("主窗口显示失败", ex);
                throw;
            }
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            ShowFatal("UI 线程未处理异常", e.Exception);
            e.Handled = true; // 关键：阻止应用立即退出，让老板看到错误
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            ShowFatal("AppDomain 致命异常" + (e.IsTerminating ? "（进程即将终止）" : ""), ex);
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            // 后台任务异常**只落盘、不弹框**：ffmpeg 管道 / 媒体回收任务里的小异常并不少见，
            // 弹框会变成"放烟花"打断直播。要排查时看「帮助 → 打开错误日志目录」里的 errors.log。
            LogFatal("后台任务未观察异常", e.Exception, snapshot: false);
            e.SetObserved();
        }

        private void ShowFatal(string title, Exception? ex)
        {
            LogFatal(title, ex);

            try
            {
                // 弹框里必须写出日志路径：老板把这段截图发过来，我就能直接去那个目录取文件，
                // 不用再问「日志在哪」。
                MessageBox.Show((ex == null ? "(无异常对象)" : ex.ToString())
                    + Environment.NewLine + Environment.NewLine
                    + "── 错误已保存到 ──" + Environment.NewLine
                    + Services.ErrorLog.Directory + Environment.NewLine
                    + "（菜单「帮助 → 打开错误日志目录」可直接打开）",
                    "Cutlance 出错 · " + title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // 极端情况下 MessageBox 自身也失败，忽略
            }
        }

        /// <summary>
        /// 建主窗**之前**，先把工程里记的输出分辨率读进 StreamService。
        ///
        /// 为什么必须提前：合成器的画面缓冲/预览位图/离屏渲染画布都在 MainWindow 构造时
        /// 按 StreamService.OutputWidth/Height 一次性分配，之后没有任何代码会去重建它们。
        /// 载入工程原本挂在 Window.Loaded 上（构造之后），所以工程里存的竖屏尺寸永远来不及
        /// 生效 —— 竖屏工程实际按 16:9 出图、点开始输出还会被拽回 1920×1080。
        ///
        /// 这里只读一个字段，不解析整份工程（ProjectService.Load 本身很轻，失败就当没设过）。
        /// </summary>
        private static void PresetOutputSizeFromProject(string path)
        {
            try
            {
                var p = Services.ProjectService.Load(path, out _);
                int w = p?.Output?.Width ?? 0;
                int h = p?.Output?.Height ?? 0;
                if (w <= 0 || h <= 0) return;

                Services.StreamService.Instance.OutputWidth = w;
                Services.StreamService.Instance.OutputHeight = h;
                Services.ErrorLog.Info($"[启动] 工程预设输出尺寸 {w}×{h}，合成器按此分配画面缓冲");
            }
            catch (Exception ex)
            {
                // 读不出来就退回默认 1920×1080，绝不能因为预设尺寸失败而起不来
                Services.ErrorLog.Info("[启动] 读取工程输出尺寸失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 把异常写进错误保存目录（见 Services/ErrorLog.cs）。
        ///
        /// 为什么不再只写 %TEMP%\cutlance-crash.log：
        ///   TEMP 在 `C:\Users\xxx\AppData\Local\Temp` 里，装到别的电脑上老板根本找不到；
        ///   而且只有"炸了"才写一行，没有环境信息、没有本次运行的时间线。
        /// 现在写三处：errors.log（累积账本）+ run-时间.log（本次运行）+ error-时间.txt（单条快照）。
        /// </summary>
        private static void LogFatal(string title, Exception? ex, bool snapshot = true)
        {
            try { Services.ErrorLog.Error(title, ex, snapshot); } catch { }
        }
    }
}
