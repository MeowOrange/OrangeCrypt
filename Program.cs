using Microsoft.Win32;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using static Serilog.Log;

namespace OrangeCrypt
{
    class Program
    {
        private static readonly string PortFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrangeCrypt",
            "port.txt");
        private const int PortRangeStart = 49152;
        private const int PortRangeEnd = 65535;
        private static TcpListener _listener;
        private static CancellationTokenSource _cts;

        [STAThread]
        public static void Main(string[] args)
        {
            LoggerInit.Initialize();
            Debug("Starting...");

            // 如果命令行参数仅一个，处理install/uninstall逻辑
            if (args.Length == 1)
            {
                string operation = args[0];
                switch (operation)
                {
                    case "/install":
                        Business.Install();
                        break;
                    case "/uninstall":
                        Business.Uninstall();
                        break;
                    default:
                        Debug("Invalid operation: {operation}", operation);
                        break;
                }
                return;
            }

            // 处理命令行启动逻辑
            if (args.Length > 0)
            {
                Debug("Started with arguments: {args}", args);
                TrySendArgsToExistingInstance(args);
                return;
            }
            else
            {
                Debug("Started without arguments, trying to claim as main instance...");
                if (TrySendArgsToExistingInstance(args))
                {
                    MessageBox.Show(
                        Utils.GetString("DuplicateInstanceMessage"),
                        Utils.GetString("MsgboxInfoTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                Debug("Checking for administrative privileges...");
                if (!Utils.IsRunningAsAdministrator())
                {
                    MessageBox.Show(
                        Utils.GetString("NoAdministrativeMessage"),
                        Utils.GetString("MsgboxInfoTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // 启动非阻塞的管道服务器
                Debug("Running as main instance, starting tcp server...");
                _cts = new CancellationTokenSource();
                var listenTask = StartTcpServer(_cts.Token);

                var app = new Application();
                AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                // 注册关机前清理逻辑
                SystemEvents.SessionEnding += (sender, e) =>
                {
                    Business.UnmountAll();
                    Thread.Sleep(3000);
                };

                // 创建托盘图标
                OtherUI.CreateNotifyIcon();

                app.Run();

                // 应用结束时清理托盘图标
                OtherUI.CleanUpNotifyIcon();

                // 清理Tcp服务端
                _cts.Cancel();
                listenTask.Wait();
            }
        }

        private static void ArgsThreadWorker(string[] args)
        {
            Debug("Init threaded worker from pipe client: {args}", args);
            if (args.Length == 0)
            {
                Debug("Empty args");
                return;
            }
            else if (args.Length != 2)
            {
                Debug("Invalid args: {args}", args);
                return;
            }

            string command = args[0];
            string path = args[1];

            string fullPath = Path.GetFullPath(path);
            switch (command)
            {
                case "/open":
                    Business.UnlockFolder(fullPath);
                    break;
                case "/optimize":
                    Business.OptimizeContainer(fullPath);
                    break;
                case "/decrypt":
                    Business.DecryptFolder(fullPath);
                    break;
                case "/encrypt":
                    Business.EncryptFolder(fullPath);
                    break;
                default:
                    throw new Exception("Invalid command");
            }
        }

        private static void WritePortToFile(int port)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PortFilePath));
            File.WriteAllText(PortFilePath, port.ToString());
        }

        private static int ReadPortFromFile()
        {
            try
            {
                return int.Parse(File.ReadAllText(PortFilePath));
            }
            catch
            {
                return 0;
            }
        }

        private static void ClearPortFile()
        {
            try { File.Delete(PortFilePath); }
            catch { }
        }

        private static bool TrySendArgsToExistingInstance(string[] args)
        {
            int port = ReadPortFromFile();
            if (port == 0) return false;

            try
            {
                using (var client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, port);
                    using (var stream = client.GetStream())
                    using (var writer = new StreamWriter(stream, Encoding.UTF8))
                    {
                        writer.WriteLine(JsonSerializer.Serialize(args));
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var json = await reader.ReadLineAsync();
                    var args = JsonSerializer.Deserialize<string[]>(json);

                    if (args != null)
                    {
                        // 创建一个新STA线程来显示对话框
                        Thread thread = new Thread(() =>
                        {
                            ArgsThreadWorker(args);
                        });

                        // 设置线程为STA模式
                        thread.SetApartmentState(ApartmentState.STA);
                        thread.Start();
                    }
                    else
                    {
                        Error("Receiving null arguments, from json: {json}", json);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"客户端处理错误: {ex.Message}");
            }
        }

        private static async Task StartTcpServer(CancellationToken ct)
        {
            int port = Utils.FindAvailablePort(PortRangeStart, PortRangeEnd);
            if (port == 0) return;

            WritePortToFile(port);
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // 创建一个组合的CancellationTokenSource，包含应用退出和超时
                    using (var timeoutCts = new CancellationTokenSource())
                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                    {
                        // 设置超时时间（例如1秒）
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(1));

                        try
                        {
                            // 等待连接或超时
                            var client = await _listener.AcceptTcpClientAsync(linkedCts.Token);
                            _ = HandleClientAsync(client, linkedCts.Token);
                        }
                        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                        {
                            // 超时后继续循环，检查主CancellationToken
                            continue;
                        }
                        catch (OperationCanceledException)
                        {
                            // 主CancellationToken触发，退出循环
                            break;
                        }
                    }
                }
            }
            finally
            {
                _listener.Stop();
                ClearPortFile();
            }
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // 处理未捕获的异常
            MessageBox.Show($"发生未处理的异常: {e.ExceptionObject}");
        }
    }
}
