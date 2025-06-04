using Hardcodet.Wpf.TaskbarNotification;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using static OrangeCrypt.Business;
using static Serilog.Log;

namespace OrangeCrypt
{
    class OtherUI
    {
        private static TaskbarIcon _notifyIcon;
        public static void CreateNotifyIcon()
        {
            MountedFolders.CollectionChanged += (sender, e) =>
            {
                UpdateMenu();
            };

            _notifyIcon = new TaskbarIcon();
            _notifyIcon.Icon = Utils.GetIcon("ProgramIcon"); // 使用默认图标，你可以替换为自己的图标
            _notifyIcon.ToolTipText = Utils.GetString("ProgramName");

            new Thread(RedrawIfNotActuallyCreated).Start();

            UpdateMenu();
        }

        public static void CleanUpNotifyIcon()
        {
            _notifyIcon.Dispose();
        }
        private static void UpdateMenu()
        {
            var contextMenu = new ContextMenu();

            // "已挂载的文件夹"菜单项
            var mountedMenuItem = new MenuItem { Header = Utils.GetString("MenuMountedFolders") };

            // 动态绑定文件夹列表
            foreach (var folder in MountedFolders)
            {
                var menuItem = new MenuItem { Header = folder };
                menuItem.Click += (s, e) =>
                {
                    HandleFolderClicked(folder);
                };
                mountedMenuItem.Items.Add(menuItem);
            }

            if (MountedFolders.Count == 0)
            {
                mountedMenuItem.Items.Add(new MenuItem { Header = "无", IsEnabled = false });
            }

            contextMenu.Items.Add(mountedMenuItem);
            contextMenu.Items.Add(new Separator());

            // "配置" 菜单项
            var configMenuItem = new MenuItem { Header = Utils.GetString("MenuConfig") };
            var doAssociationMenuItem = new MenuItem { Header = Utils.GetString("MenuConfigDoAssociation") };
            var deAssociationMenuItem = new MenuItem { Header = Utils.GetString("MenuConfigDeAssociation") };
            doAssociationMenuItem.Click += (s, e) =>
            {
                DoSystemAssociation();
                contextMenu.IsOpen = false;
                MessageBox.Show(
                    Utils.GetString("DoAssociationSuccessMessage"),
                    Utils.GetString("MsgboxInfoTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information, MessageBoxResult.OK,
                    MessageBoxOptions.DefaultDesktopOnly);
            };
            deAssociationMenuItem.Click += (s, e) =>
            {
                DeSystemAssociation();
                contextMenu.IsOpen = false;
                MessageBox.Show(
                    Utils.GetString("DeAssociationSuccessMessage"),
                    Utils.GetString("MsgboxInfoTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information, MessageBoxResult.OK,
                    MessageBoxOptions.DefaultDesktopOnly);
            };
            configMenuItem.Items.Add(doAssociationMenuItem);
            configMenuItem.Items.Add(deAssociationMenuItem);
            contextMenu.Items.Add(configMenuItem);

            // "卸载全部"菜单项
            var unmountItem = new MenuItem { Header = Utils.GetString("MenuUnmountAll") };
            unmountItem.Click += (s, e) =>
            {
                UnmountAll();
            };
            contextMenu.Items.Add(unmountItem);

            // "卸载并退出"菜单项
            var exitMenuItem = new MenuItem { Header = Utils.GetString("MenuUnmountAndExit") };
            exitMenuItem.Click += async (s, e) =>
            {
                UnmountAll();
                await Task.Delay(1000);
                Application.Current.Shutdown();
            };
            contextMenu.Items.Add(exitMenuItem);

            _notifyIcon.ContextMenu = contextMenu;
        }

        private static void HandleFolderClicked(MountedFolder folder)
        {
            Debug("Folder: {folderPath} clicked.", folder.Path);
        }

        private static void RedrawIfNotActuallyCreated()
        {
            bool canExit = false;
            while (!canExit)
            {
                if (Application.Current == null) break;
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    if (!_notifyIcon.IsTaskbarIconCreated)
                    {
                        _notifyIcon.Visibility = Visibility.Hidden;
                        _notifyIcon.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        canExit = true;
                    }
                });
                Thread.Sleep(1000);
            }
        }
    }


    public class PasswordDialog : Window, INotifyPropertyChanged
    {
        private PasswordBox passwordBox;
        private PasswordBox confirmPasswordBox;
        private CheckBox showPasswordCheckBox;
        private ProgressBar progressBar;
        private TextBlock statusText;
        private Button okButton;
        private Button cancelButton;
        public event Action<string> EncryptionStarted;

        private string _currentFile = "";
        private double _progress = 0;
        private bool _isProcessing = false;

        public string Password { get; private set; }

        public string CurrentFile
        {
            get => _currentFile;
            set
            {
                _currentFile = value;
                OnPropertyChanged(nameof(CurrentFile));
                UpdateStatusText();
            }
        }

        public double Progress
        {
            get => _progress;
            set
            {
                _progress = value;
                OnPropertyChanged(nameof(Progress));
                UpdateStatusText();
            }
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                _isProcessing = value;
                OnPropertyChanged(nameof(IsProcessing));
                UpdateControlsState();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public PasswordDialog()
        {
            // 设置窗口属性
            Title = Utils.GetString("TitleFolderEncrypt");
            Width = 400;
            Height = 260;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;

            // 创建主布局
            var stackPanel = new StackPanel { Margin = new Thickness(10) };

            // 添加密码输入框
            passwordBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 10) };
            stackPanel.Children.Add(new Label { Content = Utils.GetString("WordPassword") + ":" });
            stackPanel.Children.Add(passwordBox);

            // 添加确认密码输入框
            confirmPasswordBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 10) };
            stackPanel.Children.Add(new Label { Content = Utils.GetString("WordConfirmPassword") + ":" });
            stackPanel.Children.Add(confirmPasswordBox);

            // 添加进度条和状态文本
            progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 20, Margin = new Thickness(0, 0, 0, 5) };
            stackPanel.Children.Add(progressBar);

            statusText = new TextBlock();
            stackPanel.Children.Add(statusText);

            // 添加显示密码复选框
            showPasswordCheckBox = new CheckBox { Content = Utils.GetString("WordShowPassword"), Margin = new Thickness(0, 10, 0, 10) };
            showPasswordCheckBox.Checked += ShowPasswordCheckBox_Changed;
            showPasswordCheckBox.Unchecked += ShowPasswordCheckBox_Changed;
            stackPanel.Children.Add(showPasswordCheckBox);

            // 添加底部面板（按钮）
            var buttonPanel = new DockPanel { LastChildFill = false };

            // 取消按钮
            cancelButton = new Button { Content = Utils.GetString("WordCancel"), MinWidth = 70, Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
            cancelButton.Click += (s, e) => DialogResult = false;
            DockPanel.SetDock(cancelButton, Dock.Right);
            buttonPanel.Children.Add(cancelButton);

            // 确定按钮
            okButton = new Button { Content = Utils.GetString("WordOk"), MinWidth = 70, IsDefault = true };
            okButton.Click += OkButton_Click;
            DockPanel.SetDock(okButton, Dock.Right);
            buttonPanel.Children.Add(okButton);

            stackPanel.Children.Add(buttonPanel);
            Content = stackPanel;

            Topmost = true;
            Loaded += OnWindowLoaded;

            // 初始化状态
            UpdateStatusText();
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            Activate();
            passwordBox.Focus();
            Topmost = false;
        }
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void UpdateStatusText()
        {
            statusText.Text = string.Format(Utils.GetString("EncryptStatusFormat")!, CurrentFile, Progress);
        }

        private void UpdateControlsState()
        {
            passwordBox.IsEnabled = !IsProcessing;
            confirmPasswordBox.IsEnabled = !IsProcessing;
            showPasswordCheckBox.IsEnabled = !IsProcessing;
            okButton.IsEnabled = !IsProcessing;
            cancelButton.IsEnabled = !IsProcessing;
        }

        private void ShowPasswordCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            bool showPassword = showPasswordCheckBox.IsChecked ?? false;

            // 临时存储当前文本
            string password = "";
            string confirmPassword = "";

            if (showPassword)
            {
                // 从PasswordBox获取密码
                password = passwordBox.Password;
                confirmPassword = confirmPasswordBox.Password;
            }
            else
            {
                // 从TextBox获取密码（如果存在）
                if (passwordBox.Tag is TextBox passwordTextBox)
                    password = passwordTextBox.Text;
                if (confirmPasswordBox.Tag is TextBox confirmTextBox)
                    confirmPassword = confirmTextBox.Text;
            }

            // 清除现有内容
            passwordBox.Password = "";
            confirmPasswordBox.Password = "";

            // 切换SecureText模式
            passwordBox.Visibility = showPassword ? Visibility.Collapsed : Visibility.Visible;
            confirmPasswordBox.Visibility = showPassword ? Visibility.Collapsed : Visibility.Visible;

            if (showPassword)
            {
                var passwordTextBox = new TextBox { Text = password, Margin = new Thickness(0, 0, 0, 10) };
                var confirmTextBox = new TextBox { Text = confirmPassword, Margin = new Thickness(0, 0, 0, 10) };

                // 替换PasswordBox为TextBox
                var parent = (Panel)passwordBox.Parent;
                var index = parent.Children.IndexOf(passwordBox);
                parent.Children.RemoveAt(index);
                parent.Children.Insert(index, passwordTextBox);

                parent = (Panel)confirmPasswordBox.Parent;
                index = parent.Children.IndexOf(confirmPasswordBox);
                parent.Children.RemoveAt(index);
                parent.Children.Insert(index, confirmTextBox);

                // 保存引用以便切换回来
                passwordBox.Tag = passwordTextBox;
                confirmPasswordBox.Tag = confirmTextBox;
            }
            else
            {
                // 恢复PasswordBox
                if (passwordBox.Tag is TextBox passwordTextBox)
                {
                    passwordBox.Password = passwordTextBox.Text;

                    var parent = (Panel)passwordTextBox.Parent;
                    var index = parent.Children.IndexOf(passwordTextBox);
                    parent.Children.RemoveAt(index);
                    parent.Children.Insert(index, passwordBox);
                }

                if (confirmPasswordBox.Tag is TextBox confirmTextBox)
                {
                    confirmPasswordBox.Password = confirmTextBox.Text;

                    var parent = (Panel)confirmTextBox.Parent;
                    var index = parent.Children.IndexOf(confirmTextBox);
                    parent.Children.RemoveAt(index);
                    parent.Children.Insert(index, confirmPasswordBox);
                }
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            string password = "";
            string confirmPassword = "";

            // 获取密码（考虑显示密码的情况）
            if (showPasswordCheckBox.IsChecked == true)
            {
                if (passwordBox.Tag is TextBox passwordTextBox)
                    password = passwordTextBox.Text;
                if (confirmPasswordBox.Tag is TextBox confirmTextBox)
                    confirmPassword = confirmTextBox.Text;
            }
            else
            {
                password = passwordBox.Password;
                confirmPassword = confirmPasswordBox.Password;
            }

            if (password != confirmPassword)
            {
                MessageBox.Show("两次输入的密码不一致，请重新输入。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                MessageBox.Show("密码不能为空。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Password = password;
            IsProcessing = true;

            // 触发事件而不是关闭对话框
            EncryptionStarted?.Invoke(Password);
        }

        // 线程安全更新进度的方法
        public void UpdateProgress(double progress, string currentFile)
        {
            if (Dispatcher.CheckAccess())
            {
                // 如果在UI线程直接更新
                Progress = progress;
                CurrentFile = currentFile;
                progressBar.Value = progress;
            }
            else
            {
                // 如果不在UI线程，使用Dispatcher异步更新
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Progress = progress;
                    CurrentFile = currentFile;
                    progressBar.Value = progress;
                }), DispatcherPriority.Background);
            }
        }

        public void UpdateProgressWrapped(long bytesCopied, long totalBytes, string currentStatus)
        {
            if (totalBytes == 0)
            {
                UpdateProgress(0, currentStatus);
            }
            else
            {
                UpdateProgress(Math.Min(100, bytesCopied * 100.0 / totalBytes), currentStatus);
            }
        }

        // 完成处理的方法
        public void CompleteProcessing(bool success)
        {
            if (Dispatcher.CheckAccess())
            {
                // 如果在UI线程直接处理
                DialogResult = success;
            }
            else
            {
                // 如果不在UI线程，使用Dispatcher异步处理
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    DialogResult = success;
                }), DispatcherPriority.Background);
            }
        }
    }

    public class SimplePasswordDialog : Window
    {
        private PasswordBox passwordBox;
        private CheckBox showPasswordCheckBox;

        public string Password { get; private set; }

        public SimplePasswordDialog()
        {
            // 设置窗口属性
            Title = Utils.GetString("TitleEnterPassword");
            Width = 300;
            Height = 160;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;

            // 创建主布局
            var stackPanel = new StackPanel { Margin = new Thickness(10) };

            // 添加密码输入框
            passwordBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 10) };
            stackPanel.Children.Add(new Label { Content = Utils.GetString("WordPassword") + ":" });
            stackPanel.Children.Add(passwordBox);

            // 添加显示密码复选框
            showPasswordCheckBox = new CheckBox { Content = Utils.GetString("WordShowPassword"), Margin = new Thickness(0, 0, 0, 10) };
            showPasswordCheckBox.Checked += ShowPasswordCheckBox_Changed;
            showPasswordCheckBox.Unchecked += ShowPasswordCheckBox_Changed;
            stackPanel.Children.Add(showPasswordCheckBox);

            // 添加按钮面板
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            // 确定按钮
            var okButton = new Button { Content = Utils.GetString("WordOk"), MinWidth = 70, Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
            okButton.Click += OkButton_Click;
            buttonPanel.Children.Add(okButton);

            // 取消按钮
            var cancelButton = new Button { Content = Utils.GetString("WordCancel"), MinWidth = 70, IsCancel = true };
            cancelButton.Click += (s, e) => DialogResult = false;
            buttonPanel.Children.Add(cancelButton);

            stackPanel.Children.Add(buttonPanel);
            Content = stackPanel;

            Topmost = true;
            Loaded += OnWindowLoaded;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            Activate();
            passwordBox.Focus();
            Topmost = false;
        }
        private void ShowPasswordCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            bool showPassword = showPasswordCheckBox.IsChecked ?? false;

            // 临时存储当前密码
            string password = passwordBox.Password;

            // 清除现有内容
            passwordBox.Password = "";

            // 切换SecureText模式
            passwordBox.Visibility = showPassword ? Visibility.Collapsed : Visibility.Visible;

            if (showPassword)
            {
                // 创建并显示TextBox
                var passwordTextBox = new TextBox { Text = password, Margin = new Thickness(0, 0, 0, 10) };

                // 替换PasswordBox为TextBox
                var parent = (Panel)passwordBox.Parent;
                var index = parent.Children.IndexOf(passwordBox);
                parent.Children.RemoveAt(index);
                parent.Children.Insert(index, passwordTextBox);

                // 保存引用以便切换回来
                passwordBox.Tag = passwordTextBox;
            }
            else
            {
                // 恢复PasswordBox
                if (passwordBox.Tag is TextBox passwordTextBox)
                {
                    passwordBox.Password = passwordTextBox.Text;

                    var parent = (Panel)passwordTextBox.Parent;
                    var index = parent.Children.IndexOf(passwordTextBox);
                    parent.Children.RemoveAt(index);
                    parent.Children.Insert(index, passwordBox);
                }
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            string password;

            // 获取密码（考虑显示密码的情况）
            if (showPasswordCheckBox.IsChecked == true && passwordBox.Tag is TextBox passwordTextBox)
            {
                password = passwordTextBox.Text;
            }
            else
            {
                password = passwordBox.Password;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                MessageBox.Show("密码不能为空。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Password = password;
            DialogResult = true;
        }
    }

    public class PasswordVerificationDialog : Window, INotifyPropertyChanged
    {
        private PasswordBox passwordBox;
        private CheckBox showPasswordCheckBox;
        private ProgressBar progressBar;
        private TextBlock statusText;
        private Button okButton;
        private Button cancelButton;

        private string _currentOperation = "";
        private double _progress = 0;
        private bool _isProcessing = false;

        public string Password { get; private set; }

        public string CurrentOperation
        {
            get => _currentOperation;
            set
            {
                _currentOperation = value;
                OnPropertyChanged(nameof(CurrentOperation));
                UpdateStatusText();
            }
        }

        public double Progress
        {
            get => _progress;
            set
            {
                _progress = value;
                OnPropertyChanged(nameof(Progress));
                UpdateStatusText();
            }
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                _isProcessing = value;
                OnPropertyChanged(nameof(IsProcessing));
                UpdateControlsState();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<string> VerificationStarted;

        public PasswordVerificationDialog()
        {
            // 设置窗口属性
            Title = Utils.GetString("TitleEnterPassword");
            Width = 400;
            Height = 210;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            // 创建主布局
            var stackPanel = new StackPanel { Margin = new Thickness(10) };

            // 添加密码输入框
            passwordBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 10) };
            stackPanel.Children.Add(new Label { Content = Utils.GetString("WordPassword") + ":" });
            stackPanel.Children.Add(passwordBox);

            // 添加进度条和状态文本
            progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 20, Margin = new Thickness(0, 0, 0, 5) };
            stackPanel.Children.Add(progressBar);

            statusText = new TextBlock();
            stackPanel.Children.Add(statusText);

            // 添加显示密码复选框
            showPasswordCheckBox = new CheckBox { Content = Utils.GetString("WordShowPassword"), Margin = new Thickness(0, 10, 0, 10) };
            showPasswordCheckBox.Checked += ShowPasswordCheckBox_Changed;
            showPasswordCheckBox.Unchecked += ShowPasswordCheckBox_Changed;
            stackPanel.Children.Add(showPasswordCheckBox);

            // 添加底部面板（按钮）
            var buttonPanel = new DockPanel { LastChildFill = false };

            // 取消按钮
            cancelButton = new Button { Content = Utils.GetString("WordCancel"), MinWidth = 70, Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
            cancelButton.Click += (s, e) => DialogResult = false;
            DockPanel.SetDock(cancelButton, Dock.Right);
            buttonPanel.Children.Add(cancelButton);

            // 确定按钮
            okButton = new Button { Content = Utils.GetString("WordOk"), MinWidth = 70, IsDefault = true };
            okButton.Click += OkButton_Click;
            DockPanel.SetDock(okButton, Dock.Right);
            buttonPanel.Children.Add(okButton);

            stackPanel.Children.Add(buttonPanel);
            Content = stackPanel;

            // 初始化状态
            UpdateStatusText();

            Topmost = true;
            Loaded += OnWindowLoaded;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            Activate();
            passwordBox.Focus();
            Topmost = false;
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void UpdateStatusText()
        {
            statusText.Text = string.Format(Utils.GetString("EncryptStatusFormat")!, CurrentOperation, Progress);
        }

        public void UpdateProgressWrapped(long bytesCopied, long totalBytes, string currentStatus)
        {
            if (totalBytes == 0)
            {
                UpdateProgress(0, currentStatus);
            }
            else
            {
                UpdateProgress(Math.Min(100, bytesCopied * 100.0 / totalBytes), currentStatus);
            }
        }
        private void UpdateControlsState()
        {
            passwordBox.IsEnabled = !IsProcessing;
            showPasswordCheckBox.IsEnabled = !IsProcessing;
            okButton.IsEnabled = !IsProcessing;
            cancelButton.IsEnabled = !IsProcessing;
        }

        private void ShowPasswordCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            bool showPassword = showPasswordCheckBox.IsChecked ?? false;

            // 临时存储当前文本
            string password = "";

            if (showPassword)
            {
                password = passwordBox.Password;
            }
            else
            {
                if (passwordBox.Tag is TextBox passwordTextBox)
                    password = passwordTextBox.Text;
            }

            // 清除现有内容
            passwordBox.Password = "";

            // 切换SecureText模式
            passwordBox.Visibility = showPassword ? Visibility.Collapsed : Visibility.Visible;

            if (showPassword)
            {
                var passwordTextBox = new TextBox { Text = password, Margin = new Thickness(0, 0, 0, 10) };

                // 替换PasswordBox为TextBox
                var parent = (Panel)passwordBox.Parent;
                var index = parent.Children.IndexOf(passwordBox);
                parent.Children.RemoveAt(index);
                parent.Children.Insert(index, passwordTextBox);

                // 保存引用以便切换回来
                passwordBox.Tag = passwordTextBox;
            }
            else
            {
                // 恢复PasswordBox
                if (passwordBox.Tag is TextBox passwordTextBox)
                {
                    passwordBox.Password = passwordTextBox.Text;

                    var parent = (Panel)passwordTextBox.Parent;
                    var index = parent.Children.IndexOf(passwordTextBox);
                    parent.Children.RemoveAt(index);
                    parent.Children.Insert(index, passwordBox);
                }
            }
        }

        public void ResetForRetry()
        {
            if (Dispatcher.CheckAccess())
            {
                // 如果在UI线程直接处理
                IsProcessing = false;

                // 清空密码框但保留显示状态
                if (showPasswordCheckBox.IsChecked == true)
                {
                    if (passwordBox.Tag is TextBox passwordTextBox)
                        passwordTextBox.Text = "";
                }
                else
                {
                    passwordBox.Password = "";
                }

                // 将焦点设置回密码框
                if (showPasswordCheckBox.IsChecked == true)
                {
                    if (passwordBox.Tag is TextBox passwordTextBox)
                        passwordTextBox.Focus();
                }
                else
                {
                    passwordBox.Focus();
                }
            }
            else
            {
                // 如果不在UI线程，使用Dispatcher异步处理
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ResetForRetry();
                }), DispatcherPriority.Background);
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            string password = "";

            // 获取密码（考虑显示密码的情况）
            if (showPasswordCheckBox.IsChecked == true)
            {
                if (passwordBox.Tag is TextBox passwordTextBox)
                    password = passwordTextBox.Text;
            }
            else
            {
                password = passwordBox.Password;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                MessageBox.Show("密码不能为空。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Password = password;
            IsProcessing = true;

            // 触发验证事件
            VerificationStarted?.Invoke(Password);
        }

        // 线程安全更新进度的方法
        public void UpdateProgress(double progress, string currentOperation)
        {
            if (Dispatcher.CheckAccess())
            {
                // 如果在UI线程直接更新
                Progress = progress;
                CurrentOperation = currentOperation;
                progressBar.Value = progress;
            }
            else
            {
                // 如果不在UI线程，使用Dispatcher异步更新
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Progress = progress;
                    CurrentOperation = currentOperation;
                    progressBar.Value = progress;
                }), DispatcherPriority.Background);
            }
        }

        // 完成处理的方法
        public void CompleteVerification(bool success)
        {
            if (Dispatcher.CheckAccess())
            {
                // 如果在UI线程直接处理
                DialogResult = success;
            }
            else
            {
                // 如果不在UI线程，使用Dispatcher异步处理
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    DialogResult = success;
                }), DispatcherPriority.Background);
            }
        }
    }
}
