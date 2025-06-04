using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using static Serilog.Log;

namespace OrangeCrypt
{
    public class Business
    {
        public const string FileExtension = ".ofcrypt";
        public const string ProgID = "Ofcrypt.AssocFile";
        public static string AppName = Utils.GetString("FriendlyTypeName");

        public class MountedFolder(string path, ManualResetEventSlim resetter)
        {
            public string Path { get; set; } = path;

            public ManualResetEventSlim Resetter { get; set; } = resetter;
        }

        public static ObservableCollection<MountedFolder> MountedFolders = [];


        public static void EncryptFolder(string path)
        {
            string containerPath = Utils.GetExtFilePathFromDirectory(path);
            var dialog = new PasswordDialog();
            string password = null;

            dialog.EncryptionStarted += (pwd) =>
            {
                password = pwd;

                // 启动后台处理
                Task.Run(() =>
                {
                    try
                    {
                        string folderIsNormal = DirectoryValidator.ValidateDirectory(path);
                        if (folderIsNormal != null)
                        {
                            MessageBox.Show(
                                string.Format(Utils.GetString("AbnormalFolderMessage")!, folderIsNormal),
                                Utils.GetString("MsgboxInfoTitle"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                            dialog.CompleteProcessing(false);
                            return;
                        }

                        WrappedVhdDokanFSHelper.CopyDirectoryToNewContainer(path, containerPath, password, progressCallback: (b, t, f) =>
                        {
                            dialog.UpdateProgressWrapped(b, t, Utils.GetString("CurrentFileLabel")! + Utils.ShortenDisplayPath(f, 15));
                        });

                        // 处理完成，关闭对话框
                        dialog.CompleteProcessing(true);
                    }
                    catch (Exception e)
                    {
                        var deletedFailedFile = Utils.TryDeleteFile(containerPath);
                        if (deletedFailedFile)
                        {
                            MessageBox.Show(
                                Utils.GetString("FailedWithDeletionMessage"),
                                Utils.GetString("MsgboxInfoTitle"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        }
                        else
                        {
                            MessageBox.Show(
                                Utils.GetString("FailedWithoutDeletionMessage"),
                                Utils.GetString("MsgboxInfoTitle"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        }
                        dialog.CompleteProcessing(false);
                    }
                });
            };

            // 显示对话框 - 这会阻塞直到对话框关闭
            var result = dialog.ShowDialog();

            if (result == true)
            {
                // 删除源文件夹
                try
                {
                    Directory.Delete(path, true);
                }
                catch
                {
                    MessageBox.Show(
                        Utils.GetString("SuccessWithoutDeletionMessage"),
                        Utils.GetString("MsgboxInfoTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                MessageBox.Show(
                    string.Format(Utils.GetString("SuccessEncryptMessage")!, path),
                    Utils.GetString("MsgboxInfoTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                Information($"Encrypt done for folder: {path}");
            }
            else
            {
                Information("User canceled folder encryption");
            }
        }

        public static void UnlockFolder(string path)
        {
            if (!path.EndsWith(FileExtension))
            {
                MessageBox.Show(
                    Utils.GetString("InvalidEncryptedFileMessage"),
                    Utils.GetString("MsgboxInfoTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            // 如果是已经挂载的文件夹，直接把打开请求转发到Explorer
            if (MountedFolders.Any(x => x.Path == Utils.GetDirectoryFromExtFilePath(path))) {
                Utils.TryOpenFolder(Utils.GetDirectoryFromExtFilePath(path));
                return;
            }

            var dialog = new SimplePasswordDialog();
            if (dialog.ShowDialog() == true)
            {
                string password = dialog.Password;
                try
                {
                    bool passwordCorrect = WrappedVhdDokanFS.VerifyPassword(path, password);
                    if (!passwordCorrect)
                    {
                        MessageBox.Show(
                            Utils.GetString("WrongPasswordMessage"),
                            Utils.GetString("MsgboxInfoTitle"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }

                    string dirPath = Utils.GetDirectoryFromExtFilePath(path);
                    if (Directory.Exists(dirPath) || File.Exists(dirPath))
                    {
                        MessageBox.Show(
                            Utils.GetString("FolderAlreadyExistMessage"),
                            Utils.GetString("MsgboxInfoTitle"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }

                    var resetter = WrappedVhdDokanFSHelper.CreateOrMountAsync(dirPath, path, password, 100L * 1024 * 1024 * 1024);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MountedFolders.Add(new MountedFolder(dirPath, resetter));
                    });

                }
                catch (Exception ex)
                {
                    Error(ex.Message);
                    MessageBox.Show(
                        string.Format(Utils.GetString("UnexpectedErrorMessage")!, ex.Message),
                        Utils.GetString("MsgboxInfoTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        public static void DecryptFolder(string path)
        {
            if (!path.EndsWith(FileExtension))
            {
                MessageBox.Show(
                    Utils.GetString("InvalidEncryptedFileMessage"),
                    Utils.GetString("MsgboxInfoTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var dialog = new PasswordVerificationDialog();
            string dirPath = Utils.GetDirectoryFromExtFilePath(path);

            dialog.VerificationStarted += (password) =>
            {
                Task.Run(() =>
                {
                    try
                    {
                        bool passwordCorrect = WrappedVhdDokanFS.VerifyPassword(path, password);
                        if (!passwordCorrect)
                        {
                            MessageBox.Show(
                                Utils.GetString("WrongPasswordMessage"),
                                Utils.GetString("MsgboxInfoTitle"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                            dialog.ResetForRetry();
                            return;
                        }

                        WrappedVhdDokanFSHelper.CopyContaierToDirectory(path, dirPath, password, progressCallback: (b, t, f) =>
                        {
                            dialog.UpdateProgressWrapped(b, t, Utils.GetString("CurrentFileLabel")! + Utils.ShortenDisplayPath(f, 15));
                        });


                        dialog.CompleteVerification(true);
                    }
                    catch
                    {
                        var deletedFailedFile = Utils.TryDeleteFolder(dirPath);
                        if (deletedFailedFile)
                        {
                            MessageBox.Show(
                                Utils.GetString("DecryptFailedWithDeletionMessage"),
                                Utils.GetString("MsgboxInfoTitle"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        }
                        else
                        {
                            MessageBox.Show(
                                Utils.GetString("DecryptFailedWithoutDeletionMessage"),
                                Utils.GetString("MsgboxInfoTitle"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        }
                        dialog.CompleteVerification(false);
                    }
                });
            };

            var result = dialog.ShowDialog();

            if (result == true)
            {
                var containerDeleted = Utils.TryDeleteFile(path);
                if (containerDeleted)
                {
                    MessageBox.Show(
                        Utils.GetString("DecryptSuccessMessage"),
                        Utils.GetString("MsgboxInfoTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        Utils.GetString("DecryptSuccessWithoutDeletionMessage"),
                        Utils.GetString("MsgboxInfoTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                Information($"Encrypt done for folder: {path}");
            }
            else
            {
                Information("User canceled optimization");
            }
        }

        public static void OptimizeContainer(string path)
        {
            if (!path.EndsWith(FileExtension))
            {
                MessageBox.Show(
                    Utils.GetString("InvalidEncryptedFileMessage"),
                    Utils.GetString("MsgboxInfoTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var dialog = new PasswordVerificationDialog();

            dialog.VerificationStarted += (password) =>
            {
                Task.Run(() =>
                {
                    try
                    {
                        bool passwordCorrect = WrappedVhdDokanFS.VerifyPassword(path, password);
                        if (!passwordCorrect)
                        {
                            MessageBox.Show(
                                Utils.GetString("WrongPasswordMessage"),
                                Utils.GetString("MsgboxInfoTitle"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                            dialog.ResetForRetry();
                            return;
                        }

                        WrappedVhdDokanFSHelper.CopyCompacted(path, path + ".new", password, progressCallback: (b, t, f) =>
                        {
                            dialog.UpdateProgressWrapped(b, t, Utils.GetString("CurrentFileLabel")! + Utils.ShortenDisplayPath(f, 15));
                        });


                        File.Move(path, path + ".old");
                        File.Move(path + ".new", path);
                        File.Delete(path + ".old");

                        dialog.CompleteVerification(true);
                    }
                    catch
                    {
                        var deletedFailedFile = Utils.TryDeleteFile(path + ".new");
                        if (deletedFailedFile)
                        {
                            MessageBox.Show(
                                Utils.GetString("OptimizeFailedWithDeletionMessage"),
                                Utils.GetString("MsgboxInfoTitle"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        }
                        else
                        {
                            MessageBox.Show(
                                Utils.GetString("OptimizeFailedWithoutDeletionMessage"),
                                Utils.GetString("MsgboxInfoTitle"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        }
                        dialog.CompleteVerification(false);
                    }
                });
            };

            var result = dialog.ShowDialog();

            if (result == true)
            {
                MessageBox.Show(
                    Utils.GetString("OptimizeSuccessMessage"),
                    Utils.GetString("MsgboxInfoTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                Information($"Encrypt done for folder: {path}");
            }
            else
            {
                Information("User canceled optimization");
            }
        }

        public static void UnmountAll()
        {
            foreach (var mountedFolder in MountedFolders)
            {
                mountedFolder.Resetter.Set();
            }
        }

        public static void DoSystemAssociation()
        {
            if (!IsAdministrator())
                throw new UnauthorizedAccessException("需要管理员权限");

            string appPath = Utils.GetExecutablePath();

            // 确保路径是 .exe 而不是 .dll
            if (appPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                appPath = Path.ChangeExtension(appPath, ".exe");
            }


            // 创建文件扩展名关联
            using (RegistryKey extKey = Registry.ClassesRoot.CreateSubKey(FileExtension))
            {
                extKey.SetValue("", ProgID);
                extKey.SetValue("PerceivedType", "Document");
                extKey.SetValue("Content Type", "application/octet-stream");
            }

            // 创建ProgID注册项
            using (RegistryKey progIdKey = Registry.ClassesRoot.CreateSubKey(ProgID))
            {
                progIdKey.SetValue("", AppName);
                progIdKey.SetValue("FriendlyTypeName", AppName);

                // 设置默认图标
                using (RegistryKey iconKey = progIdKey.CreateSubKey("DefaultIcon"))
                {
                    iconKey.SetValue("", $"\"{appPath}\",0");
                }

                // 设置隐藏扩展名
                progIdKey.SetValue("NeverShowExt", "", RegistryValueKind.String);

                // 创建shell命令
                using (RegistryKey shellKey = progIdKey.CreateSubKey("shell"))
                {
                    // 解锁命令 (默认操作)
                    CreateCommand(shellKey, "open", Utils.GetString("WordUnlock") + "(ofc)", $"\"{appPath}\" /open \"%1\"");

                    // 优化命令
                    CreateCommand(shellKey, "optimize", Utils.GetString("WordOptimize") + "(ofc)", $"\"{appPath}\" /optimize \"%1\"");

                    // 永久解密命令
                    CreateCommand(shellKey, "decrypt", Utils.GetString("WordPermanentDecrypt") + "(ofc)", $"\"{appPath}\" /decrypt \"%1\"");
                }
            }

            // 为文件夹添加上下文菜单
            using (RegistryKey dirShellKey = Registry.ClassesRoot.CreateSubKey(@"Directory\shell\OfcryptEncrypt"))
            {
                dirShellKey.SetValue("", Utils.GetString("WordEncrypt") + "(ofc)");
                dirShellKey.SetValue("Icon", appPath);

                using (RegistryKey cmdKey = dirShellKey.CreateSubKey("command"))
                {
                    cmdKey.SetValue("", $"\"{appPath}\" /encrypt \"%1\"");
                }
            }
        }

        public static void DeSystemAssociation()
        {
            if (!IsAdministrator())
                throw new UnauthorizedAccessException("需要管理员权限");

            // 删除文件扩展名关联
            Registry.ClassesRoot.DeleteSubKeyTree(FileExtension, false);

            // 删除ProgID注册项
            Registry.ClassesRoot.DeleteSubKeyTree(ProgID, false);

            // 删除文件夹上下文菜单
            Registry.ClassesRoot.DeleteSubKeyTree(@"Directory\shell\OfcryptEncrypt", false);
        }

        public static void Install()
        {
            if (!IsAdministrator())
            {
                MessageBox.Show(
                    Utils.GetString("NeedAdminMessage"),
                    Utils.GetString("MsgboxInfoTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            DoSystemAssociation();

            string appPath = Utils.GetExecutablePath();

            // 确保路径是 .exe 而不是 .dll
            if (appPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                appPath = Path.ChangeExtension(appPath, ".exe");
            }

            CreateLogonTask("OfcryptMainInstance", appPath);
        }

        public static void Uninstall()
        {
            if (!IsAdministrator())
            {
                MessageBox.Show(
                    Utils.GetString("NeedAdminMessage"),
                    Utils.GetString("MsgboxInfoTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            DeSystemAssociation();

            DeleteTask("OfcryptMainInstance");
        }

        private static void CreateCommand(RegistryKey parent, string keyName, string menuText, string command)
        {
            using (RegistryKey cmdKey = parent.CreateSubKey(keyName))
            {
                cmdKey.SetValue("", menuText);

                using (RegistryKey subCmdKey = cmdKey.CreateSubKey("command"))
                {
                    subCmdKey.SetValue("", command);
                }
            }
        }

        private static bool IsAdministrator()
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static void CreateLogonTask(string taskName, string programPath)
        {
            // 构建命令
            string args = $"/create /tn \"{taskName}\" /tr \"\\\"{programPath}\\\"\" /sc onlogon /rl highest /it /f";

            // 启动进程
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                Verb = "runas", // 以管理员权限运行
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };

            try
            {
                Process.Start(startInfo)?.WaitForExit();
            }
            catch (Exception ex)
            {
                // 处理异常（如用户取消了UAC提示）
                Console.WriteLine($"创建任务失败: {ex.Message}");
            }
        }

        public static void DeleteTask(string taskName)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/delete /tn \"{taskName}\" /f",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(startInfo)?.WaitForExit();
        }
    }
}
