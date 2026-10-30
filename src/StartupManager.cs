using System;
using System.Diagnostics;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetSplitter
{
    internal static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "NetSplitter";
        private const string TaskName = "NetSplitter";

        // 管理员模式下改用"计划任务 + 最高权限"实现开机自启：
        // 注册表启动项无法带 UAC 提权，计划任务在登录时直接以管理员身份运行且不弹窗。
        public static bool HasRunEntry()
        {
            return HasRunEntryFor(ValueName);
        }

        internal static bool HasRunEntryFor(string valueName)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    string value = key.GetValue(valueName) as string;
                    return !string.IsNullOrEmpty(value);
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool IsTaskEnabled()
        {
            return TaskExists(TaskName);
        }

        public static bool IsEnabled()
        {
            return HasRunEntry() || IsTaskEnabled();
        }

        public static bool TrySet(bool enabled, out string error)
        {
            return TrySetCore(enabled, ValueName, TaskName, Application.ExecutablePath, true, out error);
        }

        internal static bool TrySetCore(bool enabled, string valueName, string taskName,
            string exePath, bool highest, out string error)
        {
            error = null;
            if (!enabled)
            {
                if (!DeleteTask(taskName, out error)) return false;
                if (!DeleteRunEntry(valueName))
                {
                    error = "无法删除注册表启动项";
                    return false;
                }
                Logger.Write("开机启动已关闭");
                return true;
            }

            if (highest && RouteManager.IsAdmin)
            {
                if (!CreateTask(taskName, exePath, out error)) return false;
                // 同一种自启方式只保留一个，避免重复启动
                DeleteRunEntry(valueName);
                Logger.Write("开机启动已开启（计划任务，最高权限）: " + exePath);
                return true;
            }

            if (!WriteRunEntry(valueName, exePath))
            {
                error = "无法写入注册表启动项";
                return false;
            }
            Logger.Write("开机启动已开启（注册表启动项）: " + exePath);
            return true;
        }

        // 之前用注册表启动项的，首次以管理员身份运行时自动迁移到计划任务
        public static void EnsureElevatedAutostart()
        {
            try
            {
                if (!RouteManager.IsAdmin) return;
                if (!HasRunEntry() || TaskExists(TaskName)) return;
                string error;
                if (CreateTask(TaskName, Application.ExecutablePath, out error) && DeleteRunEntry(ValueName))
                {
                    Logger.Write("已将开机启动迁移为计划任务（管理员权限）");
                }
                else
                {
                    Logger.Write("迁移开机启动到计划任务失败: " + error);
                }
            }
            catch (Exception ex)
            {
                Logger.Write("迁移开机启动异常: " + ex.Message);
            }
        }

        internal static string BuildCreateTaskArgs(string taskName, string exePath)
        {
            return "/Create /TN \"" + taskName + "\" /TR \"\\\"" + exePath +
                   "\\\" --autostart --elevated-child\" /SC ONLOGON /RL HIGHEST /F";
        }

        internal static bool CreateTask(string taskName, string exePath, out string error)
        {
            error = null;
            int code;
            string output;
            if (!RunSchtasks(BuildCreateTaskArgs(taskName, exePath), out code, out output))
            {
                error = output;
                return false;
            }
            if (code != 0)
            {
                error = FirstLine(output);
                return false;
            }
            return true;
        }

        internal static bool TaskExists(string taskName)
        {
            if (string.IsNullOrEmpty(taskName)) return false;
            int code;
            string output;
            if (!RunSchtasks("/Query /TN \"" + taskName + "\"", out code, out output)) return false;
            return code == 0;
        }

        internal static bool DeleteTask(string taskName, out string error)
        {
            error = null;
            if (!TaskExists(taskName)) return true;
            int code;
            string output;
            if (!RunSchtasks("/Delete /TN \"" + taskName + "\" /F", out code, out output))
            {
                error = output;
                return false;
            }
            if (code != 0)
            {
                error = FirstLine(output);
                return false;
            }
            return true;
        }

        private static bool WriteRunEntry(string valueName, string exePath)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return false;
                    key.SetValue(valueName, "\"" + exePath + "\" --autostart", RegistryValueKind.String);
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Write("写入开机启动项失败: " + ex.Message);
                return false;
            }
        }

        private static bool DeleteRunEntry(string valueName)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return true;
                    if (key.GetValue(valueName) != null) key.DeleteValue(valueName, false);
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Write("删除开机启动项失败: " + ex.Message);
                return false;
            }
        }

        private static bool RunSchtasks(string arguments, out int exitCode, out string output)
        {
            exitCode = -1;
            output = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process process = Process.Start(psi))
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(15000))
                    {
                        try { process.Kill(); }
                        catch { }
                        output = "schtasks 执行超时";
                        return false;
                    }
                    exitCode = process.ExitCode;
                    output = (stdout + " " + stderr).Trim();
                }
                return true;
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "schtasks 执行失败";
            string[] lines = text.Replace("\r", "").Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed;
            }
            return text.Trim();
        }
    }
}
