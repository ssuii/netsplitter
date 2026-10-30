using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("NetSplitter")]
[assembly: AssemblyDescription("多网卡 IP 分流与流量监控")]
[assembly: AssemblyProduct("NetSplitter")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace NetSplitter
{
    internal static class Program
    {
        private const string MutexName = "Local\\NetSplitter.SingleInstance";

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += ThreadExceptionHandler;
            AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionHandler;

            if (HasFlag(args, "--selftest")) return SelfTest.Run(GetValue(args, "--out"));
            if (HasFlag(args, "--verify-routes")) return VerifyRoutes.Launch(args);
            if (HasFlag(args, "--verify-lan")) return VerifyRoutes.LaunchLan(args);
            if (HasFlag(args, "--render")) return Render(args);
            if (HasFlag(args, "--verify-close")) return VerifyClose(args);
            if (HasFlag(args, "--verify-sort")) return VerifySort(args);
            if (HasFlag(args, "--verify-proc")) return VerifyProc(args);
            if (HasFlag(args, "--verify-config")) return VerifyConfig(args);
            if (HasFlag(args, "--verify-location")) return VerifyLocation(args);
            if (HasFlag(args, "--verify-wifi")) return VerifyWifi(args);
            if (HasFlag(args, "--render-wifi")) return RenderWifi(args);

            bool autostart = HasFlag(args, "--autostart");
            bool elevatedChild = HasFlag(args, "--elevated-child");

            if (autostart && !elevatedChild && !RouteManager.IsAdmin)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath,
                        "--autostart --elevated-child");
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    Process.Start(psi);
                    return 0;
                }
                catch (Win32Exception ex)
                {
                    Logger.Write("开机启动提权未完成（" + ex.NativeErrorCode + "），以只读模式继续");
                }
                catch (Exception ex)
                {
                    Logger.Write("开机启动提权异常: " + ex.Message);
                }
            }
            bool owned = true;
            Mutex mutex = null;
            try
            {
                bool createdNew;
                mutex = new Mutex(true, MutexName, out createdNew);
                owned = createdNew;
                if (!owned)
                {
                    try
                    {
                        owned = mutex.WaitOne(elevatedChild ? 10000 : 3000);
                    }
                    catch (AbandonedMutexException)
                    {
                        owned = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Write("创建单实例互斥体失败: " + ex.Message);
            }

            if (!owned)
            {
                if (!elevatedChild)
                {
                    MessageBox.Show("NetSplitter 已在运行，请查看任务栏托盘图标。", "NetSplitter",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                if (mutex != null) mutex.Dispose();
                return 0;
            }

            try
            {
                MainForm form = new MainForm();
                form.StartMinimized = autostart;
                Application.Run(form);
            }
            finally
            {
                if (mutex != null)
                {
                    try { mutex.ReleaseMutex(); }
                    catch { }
                    mutex.Dispose();
                }
            }
            return 0;
        }

        private static int Render(string[] args)
        {
            string path = GetValue(args, "--render");
            if (string.IsNullOrEmpty(path)) path = Path.Combine(Path.GetTempPath(), "NetSplitter-ui.png");
            path = Path.GetFullPath(path);

            using (MainForm form = new MainForm())
            {
                form.PreviewMode = true;
                if (HasFlag(args, "--demo")) form.LoadDemoData();
                form.Show();
                Pump(1800);

                using (Bitmap bmp = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    bmp.Save(path, ImageFormat.Png);
                }
                form.Hide();
            }

            Logger.Write("界面截图已保存: " + path);
            return 0;
        }

        private static int VerifyClose(string[] args)
        {
            string path = GetValue(args, "--out");
            if (string.IsNullOrEmpty(path)) path = Path.Combine(Path.GetTempPath(), "NetSplitter-close-test.txt");
            path = Path.GetFullPath(path);

            StringBuilder sb = new StringBuilder();
            bool passed;
            sb.AppendLine("关闭按钮行为验证（WM_SYSCOMMAND / SC_CLOSE，与点击右上角 × 相同）");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            using (MainForm form = new MainForm())
            {
                form.SkipAutoApply = true;
                form.Show();
                Pump(500);
                bool shown = form.Visible;

                SendMessage(form.Handle, 0x0112, (IntPtr)0xF060, IntPtr.Zero);
                Pump(400);

                bool alive = !form.IsDisposed;
                bool hidden = !form.Visible;
                bool noTaskbar = !form.ShowInTaskbar;
                passed = shown && alive && hidden && noTaskbar;

                sb.AppendLine("  发送关闭消息前窗口可见: " + (shown ? "是" : "否"));
                sb.AppendLine("  收到关闭消息后进程存活: " + (alive ? "是" : "否（被退出）"));
                sb.AppendLine("  主窗口已隐藏: " + (hidden ? "是" : "否"));
                sb.AppendLine("  任务栏按钮已隐藏: " + (noTaskbar ? "是" : "否"));
                sb.AppendLine();
                sb.AppendLine(passed ? "结果: 已最小化到托盘" : "结果: 未最小化到托盘");
            }

            bool exitOk = false;
            using (MainForm form = new MainForm())
            {
                form.SkipAutoApply = true;
                form.Show();
                Pump(400);
                MethodInfo exit = typeof(MainForm).GetMethod("ExitApplication", BindingFlags.Instance | BindingFlags.NonPublic);
                if (exit != null)
                {
                    exit.Invoke(form, null);
                    Pump(400);
                    exitOk = form.IsDisposed;
                }
                sb.AppendLine();
                sb.AppendLine("[托盘退出] 主动退出后窗口关闭: " + (exitOk ? "是" : "否（未退出）"));
            }

            passed = passed && exitOk;

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                Logger.Write("关闭行为验证: " + path);
            }
            catch (Exception ex)
            {
                Logger.Write("写入关闭行为报告失败: " + ex.Message);
            }

            return passed ? 0 : 2;
        }

        private static int VerifySort(string[] args)
        {
            string path = GetValue(args, "--out");
            if (string.IsNullOrEmpty(path)) path = Path.Combine(Path.GetTempPath(), "NetSplitter-sort-test.txt");
            path = Path.GetFullPath(path);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("流量明细排序验证（默认按网卡降序）");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            int failures = 0;

            using (MainForm form = new MainForm())
            {
                form.PreviewMode = true;
                form.LoadDemoData();
                form.Show();
                Pump(600);

                DataGridView proc = GetGrid(form, "_procGrid");
                DataGridView ip = GetGrid(form, "_ipGrid");

                sb.AppendLine();
                sb.AppendLine("[示例数据] 程序流量表网卡顺序: " + JoinSequence(proc));
                failures += CheckLine(sb, "程序流量表默认按网卡降序且同网卡相邻", IsOrdered(proc, true));
                sb.AppendLine("[示例数据] 远端 IP 表网卡顺序: " + JoinSequence(ip));
                failures += CheckLine(sb, "远端 IP 表默认按网卡降序且同网卡相邻", IsOrdered(ip, true));
                failures += CheckLine(sb, "两张表的网卡列都显示降序标记",
                    proc.Columns[1].HeaderCell.SortGlyphDirection == SortOrder.Descending &&
                    ip.Columns[1].HeaderCell.SortGlyphDirection == SortOrder.Descending);

                sb.AppendLine();
                sb.AppendLine("[实时同步] 模拟新行插入");
                proc.Rows.Clear();
                ip.Rows.Clear();
                MethodInfo syncProc = typeof(MainForm).GetMethod("SyncProcGrid", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo syncIp = typeof(MainForm).GetMethod("SyncIpGrid", BindingFlags.Instance | BindingFlags.NonPublic);

                List<ProcTrafficRow> procBatch = new List<ProcTrafficRow>();
                procBatch.Add(MakeProc("chrome", "AdapterB", 1200));
                procBatch.Add(MakeProc("WeChat", "AdapterA", 300));
                if (syncProc != null) syncProc.Invoke(form, new object[] { procBatch, new List<ProcIpRow>() });
                ProcTrafficRow procNew = MakeProc("edge", "AdapterC", 500);
                procBatch.Add(procNew);
                if (syncProc != null) syncProc.Invoke(form, new object[] { procBatch, new List<ProcIpRow>() });
                sb.AppendLine("  插入 AdapterC 后: " + JoinSequence(proc));
                failures += CheckLine(sb, "实时同步新增行按网卡降序插入", IsOrdered(proc, true) && proc.Rows.Count == 3);

                List<IpTrafficRow> ipBatch = new List<IpTrafficRow>();
                ipBatch.Add(MakeIp("203.0.113.9", "AdapterB", 900));
                ipBatch.Add(MakeIp("198.51.100.7", "AdapterA", 400));
                if (syncIp != null) syncIp.Invoke(form, new object[] { ipBatch });
                ipBatch.Add(MakeIp("192.0.2.5", "AdapterC", 700));
                if (syncIp != null) syncIp.Invoke(form, new object[] { ipBatch });
                sb.AppendLine("  插入 AdapterC 后: " + JoinSequence(ip));
                failures += CheckLine(sb, "远端 IP 表新增行按网卡降序插入", IsOrdered(ip, true) && ip.Rows.Count == 3);

                sb.AppendLine();
                sb.AppendLine("[表头切换] 点击“网卡”列头");
                ClickHeader(proc, 1);
                sb.AppendLine("  第一次点击后: " + JoinSequence(proc) + "，标记: " + proc.Columns[1].HeaderCell.SortGlyphDirection);
                failures += CheckLine(sb, "点击后切换为升序且标记正确",
                    IsOrdered(proc, false) && proc.Columns[1].HeaderCell.SortGlyphDirection == SortOrder.Ascending);
                ClickHeader(proc, 1);
                sb.AppendLine("  第二次点击后: " + JoinSequence(proc) + "，标记: " + proc.Columns[1].HeaderCell.SortGlyphDirection);
                failures += CheckLine(sb, "再次点击恢复降序",
                    IsOrdered(proc, true) && proc.Columns[1].HeaderCell.SortGlyphDirection == SortOrder.Descending);

                sb.AppendLine();
                sb.AppendLine(failures == 0 ? "结果: 全部通过" : "结果: " + failures + " 项未通过");
                form.Hide();
            }

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                Logger.Write("排序验证: " + path);
            }
            catch (Exception ex)
            {
                Logger.Write("写入排序验证报告失败: " + ex.Message);
            }

            return failures == 0 ? 0 : 2;
        }

        private static int VerifyProc(string[] args)
        {
            string path = GetValue(args, "--out");
            if (string.IsNullOrEmpty(path)) path = Path.Combine(Path.GetTempPath(), "NetSplitter-proc-test.txt");
            path = Path.GetFullPath(path);
            string tempRules = Path.Combine(Path.GetTempPath(), "NetSplitter-proc-rules-" + Guid.NewGuid().ToString("N") + ".tsv");
            string tempBind = Path.Combine(Path.GetTempPath(), "NetSplitter-proc-bind-" + Guid.NewGuid().ToString("N") + ".tsv");

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("进程 IP 展开 / 右键加规则验证");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            int failures = 0;

            try
            {
                List<AdapterInfo> adapters = AdapterService.GetAdapters();
                string a0 = adapters.Count > 0 ? adapters[0].Name : "AdapterA";
                string a1 = adapters.Count > 1 ? adapters[1].Name : a0;

                using (MainForm form = new MainForm())
                {
                    form.PreviewMode = true;
                    form.SkipAutoApply = true;
                    SetMember(form, "_store", new RuleStore(tempRules));
                    SetMember(form, "_bindStore", new ProcBindingStore(tempBind));
                    List<SplitRule> rules = GetMember(form, "_rules") as List<SplitRule>;
                    if (rules != null) rules.Clear();
                    List<ProcBinding> bindings = GetMember(form, "_bindings") as List<ProcBinding>;
                    if (bindings != null) bindings.Clear();

                    form.Show();
                    Pump(500);
                    System.Windows.Forms.Timer uiTimer = GetMember(form, "_uiTimer") as System.Windows.Forms.Timer;
                    if (uiTimer != null) uiTimer.Stop();

                    DataGridView proc = GetGrid(form, "_procGrid");
                    List<ProcTrafficRow> procs = new List<ProcTrafficRow>();
                    procs.Add(MakeProc("chrome", a0, 2400));
                    procs.Add(MakeProc("WeChat", a1, 300));
                    procs.Add(MakeProc("msedge", a0, 100));

                    List<ProcIpRow> ips = new List<ProcIpRow>();
                    ips.Add(MakeProcIp("chrome", a0, "142.250.72.14", 1900));
                    ips.Add(MakeProcIp("chrome", a0, "142.250.72.46", 500));
                    ips.Add(MakeProcIp("msedge", a0, "13.107.42.14", 60));
                    ips.Add(MakeProcIp("WeChat", a1, "203.0.113.9", 280));

                    SyncProc(form, procs, ips);
                    sb.AppendLine();
                    sb.AppendLine("[数据] 网卡: " + a0 + " / " + a1 + "，进程行: " + proc.Rows.Count);
                    failures += CheckLine(sb, "同步进程行且初始不展开", proc.Rows.Count == 3 && CountChildRows(proc) == 0);

                    string chromeKey = "chrome|" + a0;
                    int chromeIndex = FindParent(proc, chromeKey);
                    ClickCell(proc, chromeIndex, 0);
                    sb.AppendLine("[展开] chrome 子行: " + ChildRowsText(proc, chromeIndex));
                    failures += CheckLine(sb, "点击进程展开其 IP 列表（2 个）",
                        chromeIndex >= 0 && ChildCount(proc, chromeIndex) == 2 &&
                        ChildIpAt(proc, chromeIndex, 0) == "142.250.72.14" &&
                        ParentShowsExpanded(proc, chromeIndex));

                    ClickCell(proc, chromeIndex, 0);
                    failures += CheckLine(sb, "再次点击收起子行", ChildCount(proc, chromeIndex) == 0 &&
                        !ParentShowsExpanded(proc, chromeIndex));

                    ClickCell(proc, chromeIndex, 0);
                    ips.Add(MakeProcIp("chrome", a0, "142.250.73.90", 40));
                    SyncProc(form, procs, ips);
                    chromeIndex = FindParent(proc, chromeKey);
                    sb.AppendLine("  chrome 行号: " + chromeIndex + "，子行: " + ChildRowsText(proc, chromeIndex));
                    sb.AppendLine("[刷新] chrome 子行: " + ChildRowsText(proc, chromeIndex));
                    failures += CheckLine(sb, "刷新后子行跟随父行并更新（3 个）",
                        ChildCount(proc, chromeIndex) == 3 &&
                        ChildIpAt(proc, chromeIndex, 2) == "142.250.73.90");

                    ClickHeader(proc, 1);
                    chromeIndex = FindParent(proc, chromeKey);
                    failures += CheckLine(sb, "按网卡重新排序后子行仍跟随父行",
                        chromeIndex >= 0 && ChildCount(proc, chromeIndex) == 3);
                    ClickHeader(proc, 1);

                    List<ProcTrafficRow> without = new List<ProcTrafficRow>();
                    without.Add(MakeProc("WeChat", a1, 300));
                    SyncProc(form, without, ips);
                    failures += CheckLine(sb, "进程消失后父行与子行一起移除",
                        FindParent(proc, chromeKey) < 0 && CountChildRows(proc) == 0);

                    SyncProc(form, procs, ips);
                    chromeIndex = FindParent(proc, chromeKey);
                    ClickCell(proc, chromeIndex, 0);
                    chromeIndex = FindParent(proc, chromeKey);

                    ContextMenuStrip procMenu = CallMember(form, "BuildProcMenu", new object[] { proc.Rows[chromeIndex] }) as ContextMenuStrip;
                    ToolStripMenuItem addAll = procMenu == null || procMenu.Items.Count == 0 ? null : procMenu.Items[0] as ToolStripMenuItem;
                    sb.AppendLine("[菜单] 进程菜单: " + (addAll == null ? "无" : addAll.Text + "（目标 " + addAll.DropDownItems.Count + "）"));
                    failures += CheckLine(sb, "右键进程可看到加入分流规则菜单及网卡目标",
                        addAll != null && addAll.Text.Contains("3 个 IP") && addAll.DropDownItems.Count == adapters.Count);
                    if (procMenu != null) procMenu.Dispose();

                    ContextMenuStrip ipMenu = null;
                    ToolStripMenuItem addOne = null;
                    if (chromeIndex >= 0 && ChildCount(proc, chromeIndex) > 0)
                    {
                        ipMenu = CallMember(form, "BuildProcMenu", new object[] { proc.Rows[chromeIndex + 1] }) as ContextMenuStrip;
                        addOne = ipMenu == null || ipMenu.Items.Count == 0 ? null : ipMenu.Items[0] as ToolStripMenuItem;
                    }
                    sb.AppendLine("[菜单] 子 IP 菜单: " + (addOne == null ? "无" : addOne.Text));
                    failures += CheckLine(sb, "右键子 IP 可加入分流规则",
                        addOne != null && addOne.Text.Contains("142.250.72.14") && addOne.DropDownItems.Count == adapters.Count);
                    if (ipMenu != null) ipMenu.Dispose();

                    List<string> chromeIps = new List<string>();
                    chromeIps.Add("142.250.72.14");
                    chromeIps.Add("142.250.72.46");
                    CallMember(form, "AddIpRules", new object[] { chromeIps, adapters[0] });
                    List<SplitRule> current = GetMember(form, "_rules") as List<SplitRule>;
                    int afterFirst = current == null ? 0 : current.Count;
                    sb.AppendLine("[规则] 首次添加后规则数: " + afterFirst);
                    failures += CheckLine(sb, "添加规则成功并持久化到存储",
                        afterFirst == 2 && File.Exists(tempRules) &&
                        File.ReadAllText(tempRules).Contains("142.250.72.14"));
                    failures += CheckLine(sb, "规则指向所选网卡且持久化开启",
                        current != null && current.Count == 2 && current[0].AdapterId == adapters[0].Id && current[0].Persistent);

                    DataGridView rulesGrid = GetGrid(form, "_rulesGrid");
                    int procCol = FindColumnIndex(rulesGrid, "关联进程");
                    string procText = rulesGrid == null || procCol < 0 || rulesGrid.Rows.Count == 0
                        ? "" : Convert.ToString(rulesGrid.Rows[0].Cells[procCol].Value);
                    sb.AppendLine("  规则关联进程列（第 " + procCol + " 列）: " + (procText.Length == 0 ? "（空）" : procText));
                    failures += CheckLine(sb, "分流规则显示关联的监控进程",
                        procCol >= 0 && procText == "chrome");

                    CallMember(form, "AddIpRules", new object[] { chromeIps, adapters[0] });
                    current = GetMember(form, "_rules") as List<SplitRule>;
                    failures += CheckLine(sb, "重复添加被跳过", current != null && current.Count == 2);

                    sb.AppendLine();
                    sb.AppendLine("[进程 IP 自动跟踪]");
                    CheckBox persistCheck = GetMember(form, "_persistentCheck") as CheckBox;
                    bool persistOriginal = persistCheck == null || persistCheck.Checked;
                    if (persistCheck != null) persistCheck.Checked = false;

                    AdapterInfo trackTarget = adapters.Count > 1 ? adapters[1] : adapters[0];
                    CallMember(form, "StartProcTracking", new object[] { "msedge", trackTarget, new List<string>() });
                    ProcBinding msBind = CallMember(form, "FindBinding", new object[] { "msedge" }) as ProcBinding;
                    current = GetMember(form, "_rules") as List<SplitRule>;
                    sb.AppendLine("  绑定: " + (msBind == null ? "无" : msBind.Process + " → " + msBind.AdapterName + "，metric " + msBind.Metric));
                    sb.AppendLine("  " + tempBind + " 内容: " + (File.Exists(tempBind) ? File.ReadAllText(tempBind).Replace("\r", "").Replace("\n", " | ") : "（无文件）"));
                    failures += CheckLine(sb, "开启自动跟踪写入绑定并持久化（暂不新增规则）",
                        msBind != null && msBind.AdapterId == trackTarget.Id &&
                        File.Exists(tempBind) && File.ReadAllText(tempBind).Contains("msedge") &&
                        current != null && current.Count == 2);

                    int trackAdded = Convert.ToInt32(CallMember(form, "TrackNewIps", new object[] { ips }));
                    current = GetMember(form, "_rules") as List<SplitRule>;
                    SplitRule msRule = null;
                    foreach (SplitRule r in current) if (r.Match == "13.107.42.14") msRule = r;
                    sb.AppendLine("  首次跟踪新增: " + trackAdded + "，规则数 " + current.Count +
                                  "，msedge 规则: " + (msRule == null ? "无" : msRule.Match + " → " + msRule.AdapterName + "，持久化 " + msRule.Persistent));
                    failures += CheckLine(sb, "跟踪进程的新 IP 自动加入规则，且不受「持久化」勾选影响",
                        trackAdded == 1 && current.Count == 3 && msRule != null &&
                        msRule.AdapterId == trackTarget.Id && msRule.Persistent &&
                        FindRuleMatch(current, "142.250.73.90") == null &&
                        FindRuleMatch(current, "203.0.113.9") == null);

                    int repeatAdded = Convert.ToInt32(CallMember(form, "TrackNewIps", new object[] { ips }));
                    current = GetMember(form, "_rules") as List<SplitRule>;
                    failures += CheckLine(sb, "重复采样不会重复添加", repeatAdded == 0 && current.Count == 3);

                    ips.Add(MakeProcIp("msedge", a0, "13.107.55.1", 20));
                    int newAdded = Convert.ToInt32(CallMember(form, "TrackNewIps", new object[] { ips }));
                    current = GetMember(form, "_rules") as List<SplitRule>;
                    failures += CheckLine(sb, "后续新出现的 IP 持续加入规则",
                        newAdded == 1 && current.Count == 4 && FindRuleMatch(current, "13.107.55.1") != null);

                    SyncProc(form, procs, ips);
                    int msIndex = FindParent(proc, "msedge|" + a0);
                    string msText = msIndex < 0 ? "（无行）" : Convert.ToString(proc.Rows[msIndex].Cells[0].Value);
                    sb.AppendLine("  msedge 行文本: " + msText);
                    failures += CheckLine(sb, "被跟踪进程显示 ⟳ 标记", msIndex >= 0 && msText.Contains("⟳"));

                    ContextMenuStrip boundMenu = msIndex < 0 ? null : CallMember(form, "BuildProcMenu", new object[] { proc.Rows[msIndex] }) as ContextMenuStrip;
                    ToolStripMenuItem stopItem = FindMenuByPrefix(boundMenu, "停止自动跟踪");
                    ToolStripMenuItem retargetItem = FindMenuByPrefix(boundMenu, "改为自动跟踪");
                    sb.AppendLine("  已跟踪菜单: " + (stopItem == null ? "无" : stopItem.Text) + " / " + (retargetItem == null ? "无" : retargetItem.Text));
                    failures += CheckLine(sb, "已跟踪进程的右键菜单提供停止/改目标",
                        stopItem != null && stopItem.Text.Contains("msedge") && retargetItem != null &&
                        retargetItem.DropDownItems.Count == adapters.Count);
                    if (boundMenu != null) boundMenu.Dispose();

                    List<ProcBinding> reloaded = new ProcBindingStore(tempBind).Load();
                    sb.AppendLine("  重新读取绑定文件: " + reloaded.Count + " 条");
                    failures += CheckLine(sb, "绑定文件可重新读取",
                        reloaded.Count == 1 && reloaded[0].Process == "msedge" && reloaded[0].AdapterId == trackTarget.Id);

                    CallMember(form, "StopProcTracking", new object[] { "msedge" });
                    current = GetMember(form, "_rules") as List<SplitRule>;
                    bindings = GetMember(form, "_bindings") as List<ProcBinding>;
                    SyncProc(form, procs, ips);
                    msIndex = FindParent(proc, "msedge|" + a0);
                    msText = msIndex < 0 ? "" : Convert.ToString(proc.Rows[msIndex].Cells[0].Value);
                    bool bindFileClean = !File.Exists(tempBind) || !File.ReadAllText(tempBind).Contains("msedge");
                    failures += CheckLine(sb, "停止跟踪后绑定移除，已加入的规则保留",
                        (bindings == null || bindings.Count == 0) && current.Count == 4 &&
                        bindFileClean && !msText.Contains("⟳"));

                    CallMember(form, "StartProcTracking", new object[]
                    {
                        "WeChat", adapters[0], new List<string>(new string[] { "203.0.113.9" })
                    });
                    current = GetMember(form, "_rules") as List<SplitRule>;
                    SplitRule chatRule = FindRuleMatch(current, "203.0.113.9");
                    failures += CheckLine(sb, "开启跟踪时立即加入当前已有 IP，并强制持久化",
                        current.Count == 5 && chatRule != null && chatRule.AdapterId == adapters[0].Id && chatRule.Persistent);
                    CallMember(form, "StopProcTracking", new object[] { "WeChat" });

                    if (persistCheck != null) persistCheck.Checked = persistOriginal;
                    sb.AppendLine("  最终规则数: " + current.Count);
                }
            }
            catch (Exception ex)
            {
                failures++;
                sb.AppendLine();
                sb.AppendLine("验证异常: " + ex);
            }
            finally
            {
                try { File.Delete(tempRules); }
                catch { }
                try { File.Delete(tempBind); }
                catch { }
            }

            sb.AppendLine();
            sb.AppendLine(failures == 0 ? "结果: 全部通过" : "结果: " + failures + " 项未通过");

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                Logger.Write("进程展开验证: " + path);
            }
            catch (Exception ex)
            {
                Logger.Write("写入进程展开报告失败: " + ex.Message);
            }

            return failures == 0 ? 0 : 2;
        }

        private static int VerifyConfig(string[] args)
        {
            string path = GetValue(args, "--out");
            if (string.IsNullOrEmpty(path)) path = Path.Combine(Path.GetTempPath(), "NetSplitter-config-test.txt");
            path = Path.GetFullPath(path);
            string tag = Guid.NewGuid().ToString("N");
            string sourceRules = Path.Combine(Path.GetTempPath(), "NetSplitter-cfg-src-rules-" + tag + ".tsv");
            string sourceBinds = Path.Combine(Path.GetTempPath(), "NetSplitter-cfg-src-binds-" + tag + ".tsv");
            string targetRules = Path.Combine(Path.GetTempPath(), "NetSplitter-cfg-dst-rules-" + tag + ".tsv");
            string targetBinds = Path.Combine(Path.GetTempPath(), "NetSplitter-cfg-dst-binds-" + tag + ".tsv");
            string exportFile = Path.Combine(Path.GetTempPath(), "NetSplitter-cfg-export-" + tag + ".json");
            string foreignFile = Path.Combine(Path.GetTempPath(), "NetSplitter-cfg-foreign-" + tag + ".json");
            string brokenFile = Path.Combine(Path.GetTempPath(), "NetSplitter-cfg-broken-" + tag + ".json");

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("配置导出 / 导入与界面按钮验证（临时文件，不修改真实配置）");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            int failures = 0;

            try
            {
                List<AdapterInfo> adapters = AdapterService.GetAdapters();
                if (adapters.Count == 0)
                {
                    failures++;
                    sb.AppendLine("未检测到网卡，无法验证。");
                }
                else
                {
                    using (MainForm source = new MainForm())
                    {
                        source.PreviewMode = true;
                        source.SkipAutoApply = true;
                        SetMember(source, "_store", new RuleStore(sourceRules));
                        SetMember(source, "_bindStore", new ProcBindingStore(sourceBinds));
                        List<SplitRule> rules = GetMember(source, "_rules") as List<SplitRule>;
                        List<ProcBinding> binds = GetMember(source, "_bindings") as List<ProcBinding>;
                        rules.Clear();
                        binds.Clear();
                        source.Show();
                        Pump(1200);
                        System.Windows.Forms.Timer timer = GetMember(source, "_uiTimer") as System.Windows.Forms.Timer;
                        if (timer != null) timer.Stop();

                        SplitRule ipRule = new SplitRule();
                        ipRule.Match = "142.250.0.0/16";
                        ipRule.AdapterId = adapters[0].Id;
                        ipRule.AdapterName = adapters[0].Name;
                        ipRule.Persistent = true;
                        ipRule.Status = "已生效";
                        rules.Add(ipRule);

                        SplitRule domainRule = new SplitRule();
                        domainRule.Kind = RuleKind.Domain;
                        domainRule.Match = "example.com";
                        domainRule.AdapterId = adapters[adapters.Count > 1 ? 1 : 0].Id;
                        domainRule.AdapterName = adapters[adapters.Count > 1 ? 1 : 0].Name;
                        domainRule.LastIps = "93.184.216.34,93.184.216.35";
                        domainRule.Status = "已生效";
                        rules.Add(domainRule);

                        SplitRule offRule = new SplitRule();
                        offRule.Match = "203.0.113.9";
                        offRule.Enabled = false;
                        offRule.Persistent = false;
                        offRule.AdapterId = adapters[0].Id;
                        offRule.AdapterName = adapters[0].Name;
                        offRule.Status = "已停用";
                        rules.Add(offRule);

                        ProcBinding bind1 = new ProcBinding();
                        bind1.Process = "chrome.exe";
                        bind1.AdapterId = adapters[0].Id;
                        bind1.AdapterName = adapters[0].Name;
                        binds.Add(bind1);
                        ProcBinding bind2 = new ProcBinding();
                        bind2.Process = "msedge.exe";
                        bind2.AdapterId = adapters[adapters.Count > 1 ? 1 : 0].Id;
                        bind2.AdapterName = adapters[adapters.Count > 1 ? 1 : 0].Name;
                        binds.Add(bind2);

                        CallMember(source, "RebuildRulesGrid", new object[0]);
                        CallMember(source, "RefreshProcMarkers", new object[0]);
                        CallMember(source, "ExportConfig", new object[] { exportFile });

                        string json = File.ReadAllText(exportFile);
                        sb.AppendLine();
                        sb.AppendLine("[导出] " + exportFile);
                        sb.AppendLine("  大小 " + json.Length + " 字节");
                        sb.AppendLine("  片段: " + Shorten(json, 220));
                        failures += CheckLine(sb, "导出文件包含全部规则与进程跟踪",
                            json.Contains("\"Rules\"") && json.Contains("\"Processes\"") &&
                            json.Contains("chrome.exe") && json.Contains("msedge.exe") &&
                            json.Contains("142.250.0.0/16") && json.Contains("example.com") &&
                            json.Contains("93.184.216.34,93.184.216.35"));
                        failures += CheckLine(sb, "导出内容不包含本机网卡 GUID（可跨机器）",
                            !json.Contains(adapters[0].Id) &&
                            (adapters.Count <= 1 || !json.Contains(adapters[1].Id)));

                        ConfigBundle bundle = ConfigIO.Load(exportFile);
                        failures += CheckLine(sb, "导出文件可被重新解析",
                            bundle.Rules.Count == 3 && bundle.Processes.Count == 2 &&
                            bundle.Processes[0].Process == "chrome.exe");

                        bool brokenOk = false;
                        File.WriteAllText(brokenFile, "{ not a config");
                        try { ConfigIO.Load(brokenFile); }
                        catch (FormatException) { brokenOk = true; }
                        failures += CheckLine(sb, "损坏/非导出文件被拒绝", brokenOk);
                    }

                    using (MainForm target = new MainForm())
                    {
                        target.PreviewMode = true;
                        target.SkipAutoApply = true;
                        SetMember(target, "_store", new RuleStore(targetRules));
                        SetMember(target, "_bindStore", new ProcBindingStore(targetBinds));
                        List<SplitRule> rules = GetMember(target, "_rules") as List<SplitRule>;
                        List<ProcBinding> binds = GetMember(target, "_bindings") as List<ProcBinding>;
                        rules.Clear();
                        binds.Clear();
                        target.Show();
                        Pump(1200);
                        System.Windows.Forms.Timer timer = GetMember(target, "_uiTimer") as System.Windows.Forms.Timer;
                        if (timer != null) timer.Stop();

                        sb.AppendLine();
                        sb.AppendLine("[界面]");
                        bool hasApply = HasButton(target, "应用规则");
                        bool hasMinimize = HasButton(target, "最小化到托盘");
                        bool hasImport = HasButton(target, "导入配置");
                        bool hasExport = HasButton(target, "导出配置");
                        failures += CheckLine(sb, "主界面已移除「应用规则」和「最小化到托盘」按钮",
                            !hasApply && !hasMinimize);
                        failures += CheckLine(sb, "分流规则区提供导入/导出按钮", hasImport && hasExport);

                        string summary = Convert.ToString(CallMember(target, "ImportConfig", new object[] { exportFile }));
                        sb.AppendLine("  导入结果: " + summary);
                        SplitRule importedDomain = null;
                        SplitRule importedOff = null;
                        foreach (SplitRule rule in rules)
                        {
                            if (rule.Match == "example.com") importedDomain = rule;
                            if (rule.Match == "203.0.113.9") importedOff = rule;
                        }
                        ProcBinding importedChrome = null;
                        foreach (ProcBinding binding in binds)
                        {
                            if (binding.Process == "chrome.exe") importedChrome = binding;
                        }
                        failures += CheckLine(sb, "导入后规则、进程、域名与禁用状态完整",
                            rules.Count == 3 && binds.Count == 2 && importedDomain != null &&
                            importedDomain.IsDomain && importedDomain.LastIps == "93.184.216.34,93.184.216.35" &&
                            importedOff != null && !importedOff.Enabled && !importedOff.Persistent &&
                            importedChrome != null);
                        failures += CheckLine(sb, "导入时按本机网卡重新绑定 ID",
                            importedChrome != null && importedChrome.AdapterId == adapters[0].Id &&
                            importedDomain != null &&
                            importedDomain.AdapterId == adapters[adapters.Count > 1 ? 1 : 0].Id);

                        string again = Convert.ToString(CallMember(target, "ImportConfig", new object[] { exportFile }));
                        sb.AppendLine("  重复导入: " + again);
                        failures += CheckLine(sb, "重复导入不会产生重复规则/进程",
                            rules.Count == 3 && binds.Count == 2 && again.Contains("新增 0"));

                        ConfigBundle foreign = ConfigIO.Load(exportFile);
                        foreign.Rules[0].Match = "198.51.100.0/24";
                        foreign.Rules[0].Adapter = "异地机器的网卡";
                        foreign.Rules[0].AdapterKind = "ethernet";
                        foreign.Processes[0].Process = "firefox.exe";
                        foreign.Processes[0].Adapter = "异地机器的网卡";
                        foreign.Processes[0].AdapterKind = "ethernet";
                        ConfigIO.Save(foreignFile, foreign);
                        string foreignSummary = Convert.ToString(CallMember(target, "ImportConfig", new object[] { foreignFile }));
                        sb.AppendLine("  跨机器导入: " + foreignSummary);
                        SplitRule foreignRule = null;
                        ProcBinding foreignBind = null;
                        foreach (SplitRule rule in rules) if (rule.Match == "198.51.100.0/24") foreignRule = rule;
                        foreach (ProcBinding binding in binds) if (binding.Process == "firefox.exe") foreignBind = binding;
                        AdapterInfo wired = null;
                        foreach (AdapterInfo adapter in adapters) if (!adapter.IsWifi) wired = adapter;
                        bool mapped = wired == null
                            ? foreignRule != null && foreignRule.AdapterId == "" && foreignRule.AdapterName == "异地机器的网卡"
                            : foreignRule != null && foreignRule.AdapterId == wired.Id;
                        failures += CheckLine(sb, "跨机器配置按网卡类型回退匹配，进程名直接可用",
                            foreignRule != null && mapped && foreignBind != null &&
                            (wired == null || foreignBind.AdapterId == wired.Id));
                    }
                }
            }
            catch (Exception ex)
            {
                failures++;
                sb.AppendLine();
                sb.AppendLine("验证异常: " + ex);
            }
            finally
            {
                try { File.Delete(sourceRules); } catch { }
                try { File.Delete(sourceBinds); } catch { }
                try { File.Delete(targetRules); } catch { }
                try { File.Delete(targetBinds); } catch { }
                try { File.Delete(exportFile); } catch { }
                try { File.Delete(foreignFile); } catch { }
                try { File.Delete(brokenFile); } catch { }
            }

            sb.AppendLine();
            sb.AppendLine(failures == 0 ? "结果: 全部通过" : "结果: " + failures + " 项未通过");

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                Logger.Write("配置导出导入验证: " + path);
            }
            catch (Exception ex)
            {
                Logger.Write("写入配置验证报告失败: " + ex.Message);
            }

            return failures == 0 ? 0 : 2;
        }

        private static int VerifyLocation(string[] args)
        {
            string path = GetValue(args, "--out");
            if (string.IsNullOrEmpty(path)) path = Path.Combine(Path.GetTempPath(), "NetSplitter-location-test.txt");
            path = Path.GetFullPath(path);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("WLAN 查询频率验证（避免 Windows 定位图标常亮）");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            int failures = 0;

            int before = AdapterService.WifiCacheMisses;
            sb.AppendLine();
            sb.AppendLine("[监控运行 12 秒]");
            using (TrafficMonitor monitor = new TrafficMonitor(false))
            {
                monitor.Start();
                DateTime end = DateTime.UtcNow.AddSeconds(12);
                while (DateTime.UtcNow < end)
                {
                    Application.DoEvents();
                    Thread.Sleep(100);
                }
            }
            int during = AdapterService.WifiCacheMisses - before;
            sb.AppendLine("  实际查询 WLAN: " + during + " 次（含启动时 1 次）");
            failures += CheckLine(sb, "监控运行期间不再周期性查询 WLAN", during <= 1);

            sb.AppendLine();
            sb.AppendLine("[网络变化后刷新]");
            AdapterService.InvalidateWifiCache();
            int beforeRefresh = AdapterService.WifiCacheMisses;
            AdapterService.GetAdapters();
            sb.AppendLine("  重新查询: " + (AdapterService.WifiCacheMisses - beforeRefresh) + " 次");
            failures += CheckLine(sb, "网络变化后重新读取一次", AdapterService.WifiCacheMisses - beforeRefresh == 1);

            int beforeSecond = AdapterService.WifiCacheMisses;
            AdapterService.GetAdapters();
            failures += CheckLine(sb, "缓存命中后不再查询", AdapterService.WifiCacheMisses == beforeSecond);

            sb.AppendLine();
            sb.AppendLine(failures == 0 ? "结果: 全部通过" : "结果: " + failures + " 项未通过");

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                Logger.Write("WLAN 查询频率验证: " + path);
            }
            catch (Exception ex)
            {
                Logger.Write("写入定位验证报告失败: " + ex.Message);
            }

            return failures == 0 ? 0 : 2;
        }

        private static int RenderWifi(string[] args)
        {
            string path = GetValue(args, "--render-wifi");
            if (string.IsNullOrEmpty(path)) path = Path.Combine(Path.GetTempPath(), "NetSplitter-wifi.png");
            path = Path.GetFullPath(path);

            float scale = 1f;
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    scale = g.DpiX / 96f;
                }
            }
            catch
            {
            }

            string[] pair = WifiConfig.LoadPair();
            using (WifiForm form = new WifiForm(scale, pair[0], pair[1]))
            {
                form.Show();
                DateTime end = DateTime.UtcNow.AddSeconds(12);
                while (!form.ScanCompleted && DateTime.UtcNow < end) Pump(200);
                Pump(400);

                using (Bitmap bmp = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    bmp.Save(path, ImageFormat.Png);
                }
                form.Hide();
            }

            Logger.Write("Wi-Fi 窗口截图已保存: " + path);
            return 0;
        }

        private static int VerifyWifi(string[] args)
        {
            string path = GetValue(args, "--out");
            if (string.IsNullOrEmpty(path)) path = Path.Combine(Path.GetTempPath(), "NetSplitter-wifi-test.txt");
            path = Path.GetFullPath(path);
            string tempConfig = Path.Combine(Path.GetTempPath(), "NetSplitter-wifi-cfg-" + Guid.NewGuid().ToString("N") + ".tsv");
            string realConfig = WifiConfig.FilePath;
            string realRules = new RuleStore().FilePath;
            string configBefore = ReadOrEmpty(realConfig);
            string rulesBefore = ReadOrEmpty(realRules);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Wi-Fi 显示 / 快速切换 / 一键切换验证（只读，不切换真实连接）");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            int failures = 0;

            try
            {
                WifiStatus status = WifiService.ReadStatus();
                sb.AppendLine();
                sb.AppendLine("[无线状态] " + (status.Available ? "无线服务可用" : "无线服务不可用：" + status.Error));
                if (status.Available)
                {
                    sb.AppendLine("  无线接口: " + status.InterfaceId);
                    sb.AppendLine("  当前连接: " + (status.Connected ? status.Ssid + "（信号 " + status.Signal + "%，配置 " + status.ProfileName + "）" : "未连接"));
                }
                failures += CheckLine(sb, "读取 WLAN 状态未报错", status.Available && string.IsNullOrEmpty(status.Error));

                List<AdapterInfo> adapters = AdapterService.GetAdapters();
                bool hasWifi = false;
                string wifiName = "";
                foreach (AdapterInfo a in adapters)
                {
                    if (!a.IsWifi) continue;
                    hasWifi = true;
                    wifiName = a.Name;
                    sb.AppendLine("  无线网卡 " + a.Name + "（idx " + a.IfIndex + "，" + a.IPv4 + "）卡片显示: " + AdapterCard.WifiText(a));
                }
                failures += CheckLine(sb, "检测到无线网卡并生成卡片文案", !status.Available || hasWifi);
                if (status.Available && status.Connected && hasWifi)
                {
                    bool match = false;
                    foreach (AdapterInfo a in adapters)
                    {
                        if (a.IsWifi && a.WifiSsid == status.Ssid && AdapterCard.WifiText(a).Contains(status.Ssid)) match = true;
                    }
                    failures += CheckLine(sb, "卡片显示已连接的 Wi-Fi 名称 " + status.Ssid, match);
                }
                else if (status.Available && !status.Connected && hasWifi)
                {
                    bool text = false;
                    foreach (AdapterInfo a in adapters)
                    {
                        if (a.IsWifi && AdapterCard.WifiText(a).Contains("未连接")) text = true;
                    }
                    failures += CheckLine(sb, "未连接时卡片显示「Wi-Fi 未连接」", text);
                }

                List<string> profiles = WifiService.GetProfiles();
                sb.AppendLine();
                sb.AppendLine("[已保存配置] " + profiles.Count + " 个");
                foreach (string p in profiles) sb.AppendLine("  " + p);
                failures += CheckLine(sb, "读取已保存的无线配置列表", profiles != null);

                bool scanOk;
                WifiScanResult scan = WifiService.Scan(2000);
                sb.AppendLine();
                sb.AppendLine("[扫描结果] " + (string.IsNullOrEmpty(scan.Error) ? scan.Networks.Count + " 个网络" : "失败：" + scan.Error));
                int shown = 0;
                foreach (WifiNetwork net in scan.Networks)
                {
                    if (shown++ >= 10) break;
                    sb.AppendLine("  " + net.Ssid + "  信号 " + net.Signal + "%  " + net.Auth +
                                  (net.HasProfile ? "  已保存" : "") + (net.Connected ? "  已连接" : ""));
                }
                if (status.Available)
                {
                    scanOk = string.IsNullOrEmpty(scan.Error) && scan.Networks.Count > 0;
                    if (status.Connected)
                    {
                        bool found = false;
                        foreach (WifiNetwork net in scan.Networks)
                        {
                            if (net.Ssid == status.Ssid && net.Connected) found = true;
                        }
                        scanOk = scanOk && found;
                        sb.AppendLine("  当前连接 " + status.Ssid + " 出现在扫描列表中: " + (found ? "是" : "否"));
                    }
                }
                else
                {
                    scanOk = true;
                }
                failures += CheckLine(sb, "扫描返回网络列表并标记当前连接", scanOk);

                bool saveOk = WifiConfig.SavePair(tempConfig, "测试网络 A&B", "备用网络");
                string[] pairBack = WifiConfig.LoadPair(tempConfig);
                sb.AppendLine();
                sb.AppendLine("[一键切换目标] 写入 " + tempConfig);
                sb.AppendLine("  读回: A=" + pairBack[0] + "，B=" + pairBack[1]);
                failures += CheckLine(sb, "两个切换目标保存/读取一致（临时文件）",
                    saveOk && pairBack[0] == "测试网络 A&B" && pairBack[1] == "备用网络");

                string pairNone = WifiConfig.PickTarget("", "", "X", true);
                string pairSingle = WifiConfig.PickTarget("A", "", "B", true);
                string pairToB = WifiConfig.PickTarget("A", "B", "A", true);
                string pairToA = WifiConfig.PickTarget("A", "B", "B", true);
                string pairOther = WifiConfig.PickTarget("A", "B", "C", true);
                string pairOffline = WifiConfig.PickTarget("A", "B", "", false);
                sb.AppendLine("  切换判定: 未配置→[" + pairNone + "] 只有A→[" + pairSingle + "] 在A→[" + pairToB +
                              "] 在B→[" + pairToA + "] 在C→[" + pairOther + "] 未连接→[" + pairOffline + "]");
                failures += CheckLine(sb, "一键切换在 A/B 之间交替",
                    pairNone == "" && pairSingle == "A" && pairToB == "B" && pairToA == "A" &&
                    pairOther == "A" && pairOffline == "A");

                string openXml = WifiService.BuildProfileXml("测试&网络", "", 1, false);
                failures += CheckLine(sb, "开放网络配置：open/none 且无密钥",
                    openXml.Contains("<authentication>open</authentication>") &&
                    openXml.Contains("<encryption>none</encryption>") &&
                    !openXml.Contains("keyMaterial") &&
                    openXml.Contains("测试&amp;网络"));
                string wpa2Xml = WifiService.BuildProfileXml("Home", "pass<8&word", 7, true);
                failures += CheckLine(sb, "WPA2 配置：WPA2PSK/AES 且密码转义",
                    wpa2Xml.Contains("<authentication>WPA2PSK</authentication>") &&
                    wpa2Xml.Contains("<encryption>AES</encryption>") &&
                    wpa2Xml.Contains("pass&lt;8&amp;word"));
                string wpa3Xml = WifiService.BuildProfileXml("Home3", "password1", 9, true);
                failures += CheckLine(sb, "WPA3 网络使用 WPA3SAE",
                    wpa3Xml.Contains("<authentication>WPA3SAE</authentication>"));

                string tempTarget = "NetSplitter-Test-Target";
                string tempTarget2 = "NetSplitter-Test-Alt";
                using (WifiForm dialog = new WifiForm(1f, tempTarget, tempTarget2))
                {
                    dialog.Show();
                    DateTime end = DateTime.UtcNow.AddSeconds(12);
                    while (!dialog.ScanCompleted && DateTime.UtcNow < end) Pump(200);
                    Pump(300);

                    ListView list = GetMember(dialog, "_list") as ListView;
                    List<WifiNetwork> dialogNets = GetMember(dialog, "_networks") as List<WifiNetwork>;
                    Label statusLabel = GetMember(dialog, "_statusLabel") as Label;
                    Label targetLabel = GetMember(dialog, "_targetLabel") as Label;
                    ListViewItem first = list != null && list.Items.Count > 0 ? list.Items[0] : null;
                    sb.AppendLine();
                    sb.AppendLine("[切换窗口] 列表行 " + (list == null ? -1 : list.Items.Count) + "，列 " +
                                  (list == null ? 0 : list.Columns.Count) + "，扫描完成 " + (dialog.ScanCompleted ? "是" : "否"));
                    sb.AppendLine("  首行: " + (first == null ? "（空）" : first.Text + " / " + first.SubItems[1].Text + " / " + first.SubItems[3].Text));
                    sb.AppendLine("  状态: " + (statusLabel == null ? "" : statusLabel.Text));
                    sb.AppendLine("  目标: " + (targetLabel == null ? "" : targetLabel.Text));
                    failures += CheckLine(sb, "切换窗口完成扫描并填充 4 列列表",
                        dialog.ScanCompleted && list != null && list.Columns.Count == 4 &&
                        list.Items.Count == (dialogNets == null ? 0 : dialogNets.Count));
                    failures += CheckLine(sb, "切换窗口显示当前连接状态",
                        statusLabel != null && statusLabel.Text.StartsWith("当前连接：", StringComparison.Ordinal) &&
                        (!status.Connected || statusLabel.Text.Contains(status.Ssid)));
                    failures += CheckLine(sb, "切换窗口显示已配置的两个切换目标",
                        targetLabel != null && targetLabel.Text.Contains(tempTarget) &&
                        targetLabel.Text.Contains(tempTarget2) && targetLabel.Text.Contains("⇄"));
                    dialog.Hide();
                }

                using (MainForm form = new MainForm())
                {
                    form.PreviewMode = true;
                    form.SkipAutoApply = true;
                    form.Show();
                    Pump(1500);
                    System.Windows.Forms.Timer uiTimer = GetMember(form, "_uiTimer") as System.Windows.Forms.Timer;
                    if (uiTimer != null) uiTimer.Stop();
                    Button oneKey = GetMember(form, "_oneKeyWifiButton") as Button;
                    failures += CheckLine(sb, "主界面存在「一键切换」按钮", oneKey != null && oneKey.Text == "一键切换");

                    Dictionary<string, AdapterCard> cards = GetMember(form, "_cards") as Dictionary<string, AdapterCard>;
                    bool cardOk = true;
                    bool sawWifiCard = false;
                    foreach (AdapterInfo a in adapters)
                    {
                        if (!a.IsWifi) continue;
                        AdapterCard card;
                        if (cards == null || !cards.TryGetValue(a.Id, out card)) continue;
                        Label desc = GetMember(card, "_desc") as Label;
                        if (desc == null) continue;
                        sawWifiCard = true;
                        sb.AppendLine("  主界面无线卡片文本: " + desc.Text);
                        if (!desc.Text.StartsWith("Wi-Fi", StringComparison.Ordinal)) cardOk = false;
                        if (status.Connected && !desc.Text.Contains(status.Ssid)) cardOk = false;
                    }
                    if (!sawWifiCard) sb.AppendLine("  主界面暂无无线卡片（无线网卡无 IPv4 时不会显示）");
                    failures += CheckLine(sb, "主界面无线卡片实际显示 Wi-Fi 名称", cardOk);
                    form.Hide();
                }
            }
            catch (Exception ex)
            {
                failures++;
                sb.AppendLine();
                sb.AppendLine("验证异常: " + ex);
            }
            finally
            {
                try { File.Delete(tempConfig); }
                catch { }
            }

            sb.AppendLine();
            bool configUntouched = ReadOrEmpty(realConfig) == configBefore;
            bool rulesUntouched = ReadOrEmpty(realRules) == rulesBefore;
            sb.AppendLine("真实配置未被测试修改: wifi.tsv " + (configUntouched ? "未变" : "已变化") +
                          "，rules.tsv " + (rulesUntouched ? "未变" : "已变化"));
            if (!configUntouched || !rulesUntouched) failures++;

            sb.AppendLine(failures == 0 ? "结果: 全部通过" : "结果: " + failures + " 项未通过");

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                Logger.Write("Wi-Fi 验证: " + path);
            }
            catch (Exception ex)
            {
                Logger.Write("写入 Wi-Fi 验证报告失败: " + ex.Message);
            }

            return failures == 0 ? 0 : 2;
        }

        private static string ReadOrEmpty(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : "";
            }
            catch
            {
                return "";
            }
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private static int FindColumnIndex(DataGridView grid, string headerPart)
        {
            if (grid == null) return -1;
            for (int i = 0; i < grid.Columns.Count; i++)
            {
                if ((grid.Columns[i].HeaderText ?? "").Contains(headerPart)) return i;
            }
            return -1;
        }

        private static bool HasButton(Control root, string text)
        {
            if (root == null) return false;
            foreach (Control child in root.Controls)
            {
                Button button = child as Button;
                if (button != null && button.Text == text) return true;
                if (HasButton(child, text)) return true;
            }
            return false;
        }

        private static ToolStripMenuItem FindMenuByPrefix(ContextMenuStrip menu, string prefix)        {
            if (menu == null) return null;
            foreach (ToolStripItem item in menu.Items)
            {
                ToolStripMenuItem entry = item as ToolStripMenuItem;
                if (entry != null && entry.Text.StartsWith(prefix, StringComparison.Ordinal)) return entry;
            }
            return null;
        }

        private static SplitRule FindRuleMatch(List<SplitRule> rules, string match)
        {
            if (rules == null) return null;
            foreach (SplitRule rule in rules)
            {
                if (rule.Match == match) return rule;
            }
            return null;
        }

        private static void SyncProc(MainForm form, List<ProcTrafficRow> procs, List<ProcIpRow> ips)
        {
            CallMember(form, "SyncProcGrid", new object[] { procs, ips });
            Application.DoEvents();
        }

        private static ProcIpRow MakeProcIp(string process, string adapter, string ip, double rate)
        {
            ProcIpRow row = new ProcIpRow();
            row.Process = process;
            row.Adapter = adapter;
            row.RemoteIp = ip;
            row.RxRate = rate;
            row.TxRate = rate / 5;
            row.RxTotal = (ulong)(rate * 1000);
            row.TxTotal = (ulong)(rate * 200);
            return row;
        }

        private static int FindParent(DataGridView grid, string key)
        {
            for (int i = 0; i < grid.Rows.Count; i++)
            {
                if ((grid.Rows[i].Tag as string) == key) return i;
            }
            return -1;
        }

        private static bool IsChildRow(DataGridViewRow row)
        {
            string tag = row.Tag as string;
            return tag != null && tag.StartsWith("ip:", StringComparison.Ordinal);
        }

        private static int ChildCount(DataGridView grid, int parentIndex)
        {
            int count = 0;
            for (int i = parentIndex + 1; i < grid.Rows.Count; i++)
            {
                if (!IsChildRow(grid.Rows[i])) break;
                count++;
            }
            return count;
        }

        private static int CountChildRows(DataGridView grid)
        {
            int count = 0;
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (IsChildRow(row)) count++;
            }
            return count;
        }

        private static string ChildIpAt(DataGridView grid, int parentIndex, int childIndex)
        {
            int index = parentIndex + 1 + childIndex;
            if (index >= grid.Rows.Count) return "";
            string text = grid.Rows[index].Cells[0].Value as string;
            return (text ?? "").Trim();
        }

        private static string ChildRowsText(DataGridView grid, int parentIndex)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < ChildCount(grid, parentIndex); i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ChildIpAt(grid, parentIndex, i));
            }
            return sb.Length == 0 ? "（无）" : sb.ToString();
        }

        private static bool ParentShowsExpanded(DataGridView grid, int parentIndex)
        {
            string text = grid.Rows[parentIndex].Cells[0].Value as string;
            return text != null && text.StartsWith("▾", StringComparison.Ordinal);
        }

        private static void ClickCell(DataGridView grid, int rowIndex, int columnIndex)
        {
            if (rowIndex < 0) return;
            MethodInfo click = typeof(DataGridView).GetMethod("OnCellClick", BindingFlags.Instance | BindingFlags.NonPublic);
            if (click == null) return;
            click.Invoke(grid, new object[] { new DataGridViewCellEventArgs(columnIndex, rowIndex) });
        }

        private static void ClickHeader(DataGridView grid, int columnIndex)
        {
            MethodInfo click = typeof(DataGridView).GetMethod("OnColumnHeaderMouseClick", BindingFlags.Instance | BindingFlags.NonPublic);
            if (click == null) return;
            DataGridViewCellMouseEventArgs e = new DataGridViewCellMouseEventArgs(columnIndex, -1, 5, 5,
                new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
            click.Invoke(grid, new object[] { e });
        }

        private static object GetMember(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(target);
        }

        private static void SetMember(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null) field.SetValue(target, value);
        }

        private static object CallMember(object target, string name, object[] parameters)
        {
            int wanted = parameters == null ? 0 : parameters.Length;
            MethodInfo method = null;
            foreach (MethodInfo candidate in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (candidate.Name != name) continue;
                if (candidate.GetParameters().Length != wanted) continue;
                method = candidate;
                break;
            }
            return method == null ? null : method.Invoke(target, parameters);
        }

        private static DataGridView GetGrid(MainForm form, string name)
        {
            FieldInfo info = typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return info == null ? null : info.GetValue(form) as DataGridView;
        }

        private static int CompareAdapters(string a, string b)
        {
            return string.Compare(a ?? "", b ?? "", StringComparison.CurrentCultureIgnoreCase);
        }

        private static List<string> AdapterSequence(DataGridView grid)
        {
            List<string> list = new List<string>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                string text = row.Cells[1].Value as string;
                list.Add(text ?? "");
            }
            return list;
        }

        private static string JoinSequence(DataGridView grid)
        {
            List<string> seq = AdapterSequence(grid);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < seq.Count; i++)
            {
                if (i > 0) sb.Append(" > ");
                sb.Append(seq[i]);
            }
            return sb.ToString();
        }

        private static bool IsOrdered(DataGridView grid, bool desc)
        {
            List<string> seq = AdapterSequence(grid);
            for (int i = 1; i < seq.Count; i++)
            {
                int cmp = CompareAdapters(seq[i - 1], seq[i]);
                if (desc ? cmp < 0 : cmp > 0) return false;
            }

            HashSet<string> closed = new HashSet<string>();
            string last = null;
            foreach (string adapter in seq)
            {
                if (adapter == last) continue;
                if (last != null) closed.Add(last);
                if (closed.Contains(adapter)) return false;
                last = adapter;
            }
            return true;
        }

        private static ProcTrafficRow MakeProc(string process, string adapter, double rate)
        {
            ProcTrafficRow row = new ProcTrafficRow();
            row.Process = process;
            row.Adapter = adapter;
            row.RxRate = rate;
            row.TxRate = rate / 4;
            row.RxTotal = (ulong)(rate * 100);
            row.TxTotal = (ulong)(rate * 20);
            return row;
        }

        private static IpTrafficRow MakeIp(string ip, string adapter, double rate)
        {
            IpTrafficRow row = new IpTrafficRow();
            row.RemoteIp = ip;
            row.Adapter = adapter;
            row.RxRate = rate;
            row.TxRate = rate / 4;
            row.RxTotal = (ulong)(rate * 100);
            row.TxTotal = (ulong)(rate * 20);
            return row;
        }

        private static int CheckLine(StringBuilder sb, string name, bool passed)
        {
            sb.AppendLine((passed ? "  [通过] " : "  [失败] ") + name);
            return passed ? 0 : 1;
        }

        private static void Pump(int milliseconds)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < end)
            {
                Application.DoEvents();
                Thread.Sleep(30);
            }
        }

        private static bool HasFlag(string[] args, string name)
        {
            if (args == null) return false;
            foreach (string arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string GetValue(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }

        private static void ThreadExceptionHandler(object sender, ThreadExceptionEventArgs e)
        {
            Logger.Write("未处理异常: " + e.Exception);
            MessageBox.Show("发生未处理异常，详情已写入日志：\n" + Logger.Dir + "\n\n" + e.Exception.Message,
                "NetSplitter", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static void UnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs e)
        {
            Logger.Write("致命异常: " + e.ExceptionObject);
        }
    }
}
