using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace OrangeCrypt
{
    public static class Utils
    {
        private static ResourceManager resMan = new ResourceManager("OrangeCrypt.Properties.Resources",
                                                  Assembly.GetExecutingAssembly());
        private static CultureInfo ci = GetDefaultCulture();

        private static CultureInfo GetDefaultCulture()
        {
            // 检查是否是简体中文环境
            if (CultureInfo.CurrentUICulture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ||
                CultureInfo.CurrentCulture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
            {
                return new CultureInfo("zh-CN");
            }
            // 默认返回en-US
            return new CultureInfo("en-US");
        }

        public static string? GetString(string name)
        {
            return resMan.GetString(name, ci);
        }

        public static Icon? GetIcon(string name)
        {
            object resource = resMan.GetObject(name, ci);
            if (resource is Icon icon)
            {
                return icon;
            }
            throw new InvalidOperationException("No Icons Found.");
        }

        public static string DisplayFormatBytes(long bytes)
        {
            const long OneKb = 1024;
            const long OneMb = OneKb * 1024;
            const long OneGb = OneMb * 1024;
            const long OneTb = OneGb * 1024;

            return bytes switch
            {
                < OneKb => $"{bytes} B",
                >= OneKb and < OneMb => $"{bytes / (double)OneKb:N1} KB",
                >= OneMb and < OneGb => $"{bytes / (double)OneMb:N1} MB",
                >= OneGb and < OneTb => $"{bytes / (double)OneGb:N1} GB",
                >= OneTb => $"{bytes / (double)OneTb:N1} TB",
            };
        }

        public static int FindAvailablePort(int portRangeStart, int portRangeEnd)
        {
            for (int port = portRangeStart; port <= portRangeEnd; port++)
            {
                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    listener.Stop();
                    return port;
                }
                catch (SocketException)
                {
                    // 端口被占用，继续尝试下一个
                }
            }
            return 0;
        }

        public static string GetExecutablePath()
        {
            // 方法1：优先使用 EntryAssembly
            var entryAssembly = Assembly.GetEntryAssembly();
            if (entryAssembly != null && !entryAssembly.Location.EndsWith(".dll"))
            {
                return entryAssembly.Location;
            }

            // 方法2：使用 Process.GetCurrentProcess()
            try
            {
                using (var process = System.Diagnostics.Process.GetCurrentProcess())
                {
                    return process.MainModule.FileName;
                }
            }
            catch
            {
                // 方法3：使用 AppDomain 当前域
                return Assembly.GetExecutingAssembly().Location;
            }
        }

        public static bool IsRunningAsAdministrator()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static string GetExtFilePathFromDirectory(string directoryPath)
        {
            // 去除路径末尾的斜杠（如果有的话），以便正确添加后缀
            string trimmedPath = directoryPath.TrimEnd('\\', '/');

            // 检查传入的是否是盘符（如 "C:" 或 "D:"）
            if (trimmedPath.Length == 2 && char.IsLetter(trimmedPath[0]) && trimmedPath[1] == ':')
            {
                throw new ArgumentException("Directory path cannot be a volume", nameof(directoryPath));
            }

            // 添加.ofcrypt后缀并返回
            return trimmedPath + Business.FileExtension;
        }

        public static string ShortenDisplayPath(string path, int length)
        {
            string processedString;

            if (path.Length > length)
            {
                processedString = "..." + path.Substring(path.Length - length);
            }
            else
            {
                processedString = path;
            }

            return processedString;
        }

        public static string GetDirectoryFromExtFilePath(string extFilePath)
        {
            if (string.IsNullOrEmpty(extFilePath))
            {
                throw new ArgumentNullException(nameof(extFilePath), "File path cannot be null or empty");
            }

            if (!extFilePath.EndsWith(Business.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"File path must end with {Business.FileExtension} extension");
            }

            return extFilePath.Substring(0, extFilePath.Length - Business.FileExtension.Length);
        }

        public static bool TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool TryDeleteFolder(string path)
        {
            try
            {
                Directory.Delete(path, true);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void TryOpenFolder(string folderPath)
        {
            try
            {
                // 检查文件夹是否存在
                if (Directory.Exists(folderPath))
                {
                    // 直接启动资源管理器打开文件夹
                    Process.Start("explorer.exe", folderPath);
                }
            }
            catch { }
        }

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);
    }

    public static class LoggerInit
    {

        static LoggerInit()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.Debug()
                .CreateLogger();
        }

        public static void Initialize()
        {
            Log.Information("Logger initialized");
        }
    }
}
