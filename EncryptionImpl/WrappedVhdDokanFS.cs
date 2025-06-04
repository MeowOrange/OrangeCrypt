using DiscUtils;
using DiscUtils.Ntfs;
using DiscUtils.Partitions;
using DiscUtils.Streams;
using DiscUtils.Vhd;
using DokanNet;
using DokanNet.Logging;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using static OrangeCrypt.FolderNtfsCopier;
using FileAccess = System.IO.FileAccess;
using static Serilog.Log;

namespace OrangeCrypt
{
    class WrappedVhdDokanFS : IDokanOperations
    {
        private NtfsFileSystem _fs;
        private FileStream _container;

        private readonly Dictionary<string, Stream> _openFiles = [];

        public long diskSize;
        public const int headerReservedSize = 4096; // 4K 预留空间
        public WrappedVhdDokanFS(
            string containerPath,
            string password,
            long diskSize = 100L * 1024 * 1024 * 1024)  // 100GB 虚拟磁盘大小
        {
            this.diskSize = diskSize;

            // 若containerPath不存在，则创建
            if (!File.Exists(containerPath))
            {
                CreateContainer(containerPath, password, diskSize);
            }

            (_container, _fs) = LoadFs(containerPath, password);
        }

        public static bool CreateContainer(string containerPath, string password, long diskSize)
        {
            byte[] creatingKey = new byte[64];
            RandomNumberGenerator.Fill(creatingKey);

            // 创建文件并预留4K空间
            using (var fs = new FileStream(containerPath, FileMode.Create, FileAccess.ReadWrite))
            {
                byte[] encryptedCreatingKey = StringPasswordEncryption.Encrypt(creatingKey, password);
                int lengthEncKey = 96;
                fs.Write(encryptedCreatingKey, 0, lengthEncKey);

                byte[] hashedPassword = EncryptionImpl.PasswordHasher.HashPassword(password);
                int lengthHash = 64;
                fs.Write(hashedPassword, 0, hashedPassword.Length);
                Debug("Hashed Password is: {hp}", hashedPassword);

                // 剩余的预留空间填充随机字节
                byte[] reservedSpace = new byte[headerReservedSize - lengthEncKey - lengthHash];
                RandomNumberGenerator.Fill(reservedSpace);
                fs.Write(reservedSpace, 0, reservedSpace.Length);
                fs.Flush();
            }

            // 使用DiscUtils创建VHDX
            using (var fs = new FileStream(containerPath, FileMode.Open, FileAccess.ReadWrite))
            {
                // 跳过预留的4K空间
                // fs.Seek(headerReservedSize, SeekOrigin.Begin);

                OffsetStream offsetStream = new OffsetStream(fs, headerReservedSize);

                EncryptedStreamOpt encStream = new EncryptedStreamOpt(offsetStream, creatingKey);

                // 创建VHDX磁盘
                var disk = Disk.InitializeDynamic(encStream, Ownership.None, diskSize);

                BiosPartitionTable.Initialize(disk, WellKnownPartitionType.WindowsNtfs);
                var volMgr = new VolumeManager(disk);
                _ = NtfsFileSystem.Format(volMgr.GetLogicalVolumes()[0], null, new NtfsFormatOptions());
            }

            // Console.WriteLine($"成功创建VHDX文件: {containerPath}");
            // Console.WriteLine($"文件头部预留了 {headerReservedSize / 1024}KB 空间");
            return true;
        }

        public static bool VerifyPassword(string containerPath, string password)
        {
            using (var fs = new FileStream(containerPath, FileMode.Open, FileAccess.Read))
            {
                byte[] hashedPassword = new byte[64];
                fs.Seek(96, SeekOrigin.Begin);
                fs.Read(hashedPassword, 0, hashedPassword.Length);
                Debug("Read Hashed Password: {hp}", hashedPassword);
                return EncryptionImpl.PasswordHasher.VerifyPassword(password, hashedPassword);
            }
        }

        public static (FileStream, NtfsFileSystem) LoadFs(string containerPath, string password)
        {
            var _container = new FileStream(containerPath, FileMode.Open, FileAccess.ReadWrite);

            byte[] encryptedKey = new byte[96];
            _container.Read(encryptedKey, 0, encryptedKey.Length);
            var key = StringPasswordEncryption.Decrypt(encryptedKey, password);

            // _container.Seek(headerReservedSize, SeekOrigin.Begin);

            OffsetStream offsetStream = new OffsetStream(_container, headerReservedSize);

            EncryptedStreamOpt encStream = new EncryptedStreamOpt(offsetStream, key);

            var disk = new Disk(encStream, Ownership.None);
            // var disk = Disk.InitializeDynamic(_container, Ownership.None, diskSize);
            VolumeManager volumeManager = new VolumeManager(disk);
            VolumeInfo volume = volumeManager.GetLogicalVolumes()[0];

            return (_container, new NtfsFileSystem(volume.Open()));
        }

        public void Cleanup(string fileName, IDokanFileInfo info)
        {
            lock (_fs)
            {
                // 清理打开的文件
                if (_openFiles.ContainsKey(fileName))
                {
                    _openFiles[fileName].Dispose();
                    _openFiles.Remove(fileName);
                }

                if (info.DeletePending)
                {
                    if (info.IsDirectory)
                    {
                        _fs.DeleteDirectory(fileName);
                    }
                    else
                    {
                        _fs.DeleteFile(fileName);
                    }
                }
            }
        }

        public void CloseFile(string fileName, IDokanFileInfo info)
        {
            lock (_fs)
            {
                // 关闭文件
                if (_openFiles.ContainsKey(fileName))
                {
                    _openFiles[fileName].Dispose();
                    _openFiles.Remove(fileName);
                }
            }
        }

        public NtStatus CreateFile(
            string fileName,
            DokanNet.FileAccess access,
            FileShare share,
            FileMode mode,
            FileOptions options,
            FileAttributes attributes,
            IDokanFileInfo info)
        {
            try
            {
                lock (_fs)
                {
                    // 处理根目录
                    if (fileName == "\\")
                    {
                        info.IsDirectory = true;
                        return DokanResult.Success;
                    }

                    // 检查是否是已存在的目录
                    if (_fs.DirectoryExists(fileName))
                    {
                        info.IsDirectory = true;
                        return DokanResult.Success;
                    }

                    // 检查是否是目录创建请求
                    if (info.IsDirectory)
                    {
                        Console.WriteLine($"Creating Directory: {fileName}");
                        switch (mode)
                        {
                            case FileMode.CreateNew:
                                if (_fs.DirectoryExists(fileName))
                                    return DokanResult.FileExists;
                                goto case FileMode.Create;

                            case FileMode.Create:
                                _fs.CreateDirectory(fileName);
                                info.IsDirectory = true;
                                return DokanResult.Success;

                            case FileMode.OpenOrCreate:
                                if (!_fs.DirectoryExists(fileName))
                                    _fs.CreateDirectory(fileName);
                                info.IsDirectory = true;
                                return DokanResult.Success;

                            case FileMode.Open:
                                if (!_fs.DirectoryExists(fileName))
                                    return DokanResult.PathNotFound;
                                info.IsDirectory = true;
                                return DokanResult.Success;

                            default:
                                return DokanResult.AccessDenied;
                        }
                    }

                    // 处理文件创建/打开
                    switch (mode)
                    {
                        case FileMode.CreateNew:
                            if (_fs.FileExists(fileName))
                                return DokanResult.FileExists;
                            goto case FileMode.Create;

                        case FileMode.Create:
                            using (var stream = _fs.OpenFile(fileName, FileMode.Create, FileAccess.ReadWrite))
                            {
                                // 文件已创建，不需要保持打开
                            }
                            return DokanResult.Success;

                        case FileMode.OpenOrCreate:
                            if (!_fs.FileExists(fileName))
                            {
                                using (var stream = _fs.OpenFile(fileName, FileMode.Create, FileAccess.ReadWrite))
                                {
                                    // 文件已创建
                                }
                            }
                            return DokanResult.Success;

                        case FileMode.Open:
                            if (!_fs.FileExists(fileName))
                                return DokanResult.FileNotFound;
                            return DokanResult.Success;

                        case FileMode.Truncate:
                            if (!_fs.FileExists(fileName))
                                return DokanResult.FileNotFound;

                            using (var stream = _fs.OpenFile(fileName, FileMode.Truncate, FileAccess.Write))
                            {
                                // 文件已截断
                            }
                            return DokanResult.Success;

                        case FileMode.Append:
                            // 不支持Append模式
                            return DokanResult.NotImplemented;
                    }

                    if (options.HasFlag(FileOptions.DeleteOnClose))
                    {
                        info.DeletePending = true;
                    }

                    return DokanResult.Success;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CreateFile error: {ex}");
                return DokanResult.Error;
            }
        }


        public NtStatus ReadFile(
            string fileName,
            byte[] buffer,
            out int bytesRead,
            long offset,
            IDokanFileInfo info)
        {
            bytesRead = 0;

            try
            {
                lock (_fs)
                {
                    if (!_fs.FileExists(fileName))
                        return DokanResult.FileNotFound;

                    // 获取或创建文件流
                    if (!_openFiles.TryGetValue(fileName, out var stream))
                    {
                        stream = _fs.OpenFile(fileName, FileMode.Open, FileAccess.Read);
                        _openFiles[fileName] = stream;
                    }

                    lock (stream)
                    {
                        stream.Position = offset;
                        bytesRead = stream.Read(buffer, 0, buffer.Length);
                    }

                    return DokanResult.Success;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ReadFile error: {ex}");
                return DokanResult.Error;
            }
        }

        public NtStatus WriteFile(
            string fileName,
            byte[] buffer,
            out int bytesWritten,
            long offset,
            IDokanFileInfo info)
        {
            bytesWritten = 0;

            try
            {
                lock (_fs)
                {
                    if (!_fs.FileExists(fileName))
                        return DokanResult.FileNotFound;

                    // 获取或创建文件流
                    if (!_openFiles.TryGetValue(fileName, out var stream))
                    {
                        stream = _fs.OpenFile(fileName, FileMode.OpenOrCreate, FileAccess.Write);
                        _openFiles[fileName] = stream;
                    }

                    lock (stream)
                    {
                        stream.Position = offset;
                        stream.Write(buffer, 0, buffer.Length);
                        bytesWritten = buffer.Length;
                    }

                    return DokanResult.Success;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WriteFile error: {ex}");
                return DokanResult.Error;
            }
        }

        public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
        {
            try
            {
                lock (_fs)
                {
                    if (_openFiles.TryGetValue(fileName, out var stream))
                    {
                        stream.Flush();
                    }
                    return DokanResult.Success;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FlushFileBuffers error: {ex}");
                return DokanResult.Error;
            }
        }

        public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
        {
            try
            {
                if (!_fs.FileExists(fileName))
                    return DokanResult.FileNotFound;

                using (var stream = _fs.OpenFile(fileName, FileMode.Open, System.IO.FileAccess.Write))
                {
                    stream.SetLength(length);
                }
                return DokanResult.Success;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SetEndOfFile error: {ex}");
                return DokanResult.Error;
            }
        }

        public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
        {
            // 在大多数文件系统中，分配大小和文件大小是相同的
            return SetEndOfFile(fileName, length, info);
        }


        #region 文件和目录信息

        public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
        {
            fileInfo = new FileInformation();

            try
            {
                lock (_fs)
                {
                    if (fileName == "\\")
                    {
                        // 根目录
                        fileInfo.Attributes = FileAttributes.Directory;
                        fileInfo.CreationTime = DateTime.Now;
                        fileInfo.LastAccessTime = DateTime.Now;
                        fileInfo.LastWriteTime = DateTime.Now;
                        return DokanResult.Success;
                    }

                    if (_fs.DirectoryExists(fileName))
                    {
                        var dirInfo = _fs.GetDirectoryInfo(fileName);
                        fileInfo.Attributes = dirInfo.Attributes;
                        fileInfo.CreationTime = dirInfo.CreationTime;
                        fileInfo.LastAccessTime = dirInfo.LastAccessTime;
                        fileInfo.LastWriteTime = dirInfo.LastWriteTime;
                        fileInfo.Length = 0;
                        return DokanResult.Success;
                    }

                    if (_fs.FileExists(fileName))
                    {
                        var fileInfoDisc = _fs.GetFileInfo(fileName);
                        fileInfo.Attributes = fileInfoDisc.Attributes;
                        fileInfo.CreationTime = fileInfoDisc.CreationTime;
                        fileInfo.LastAccessTime = fileInfoDisc.LastAccessTime;
                        fileInfo.LastWriteTime = fileInfoDisc.LastWriteTime;
                        fileInfo.Length = fileInfoDisc.Length;
                        return DokanResult.Success;
                    }

                    return DokanResult.FileNotFound;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GetFileInformation error: {ex}");
                return DokanResult.Error;
            }
        }

        public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
        {
            files = new List<FileInformation>();

            try
            {
                lock (_fs)
                {
                    if (!_fs.DirectoryExists(fileName))
                        return DokanResult.PathNotFound;

                    var dirInfo = _fs.GetDirectoryInfo(fileName);
                    foreach (var entry in dirInfo.GetFileSystemInfos())
                    {
                        var fileInfo = new FileInformation
                        {
                            FileName = entry.Name,
                            Attributes = entry.Attributes,
                            CreationTime = entry.CreationTime,
                            LastAccessTime = entry.LastAccessTime,
                            LastWriteTime = entry.LastWriteTime
                        };

                        if ((entry.Attributes & FileAttributes.Directory) != FileAttributes.Directory)
                        {
                            fileInfo.Length = _fs.GetFileLength(entry.FullName);
                        }

                        files.Add(fileInfo);
                    }

                    return DokanResult.Success;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FindFiles error: {ex}");
                return DokanResult.Error;
            }
        }

        public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
        {
            // 我们这里不支持NTFS的alternate data streams
            streams = new List<FileInformation>();
            return DokanResult.NotImplemented;
        }

        #endregion

        #region 文件和目录属性

        public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
        {
            try
            {
                lock (_fs)
                {
                    if (_fs.FileExists(fileName))
                    {
                        _fs.SetAttributes(fileName, attributes);
                        return DokanResult.Success;
                    }

                    if (_fs.DirectoryExists(fileName))
                    {
                        _fs.SetAttributes(fileName, attributes);
                        return DokanResult.Success;
                    }

                    return DokanResult.FileNotFound;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SetFileAttributes error: {ex}");
                return DokanResult.Error;
            }
        }

        public NtStatus SetFileTime(
            string fileName,
            DateTime? creationTime,
            DateTime? lastAccessTime,
            DateTime? lastWriteTime,
            IDokanFileInfo info)
        {
            try
            {
                lock (_fs)
                {
                    if (_fs.FileExists(fileName))
                    {
                        var fileInfo = _fs.GetFileInfo(fileName);
                        if (creationTime.HasValue)
                            fileInfo.CreationTime = creationTime.Value;
                        if (lastAccessTime.HasValue)
                            fileInfo.LastAccessTime = lastAccessTime.Value;
                        if (lastWriteTime.HasValue)
                            fileInfo.LastWriteTime = lastWriteTime.Value;
                        return DokanResult.Success;
                    }

                    if (_fs.DirectoryExists(fileName))
                    {
                        var dirInfo = _fs.GetDirectoryInfo(fileName);
                        if (creationTime.HasValue)
                            dirInfo.CreationTime = creationTime.Value;
                        if (lastAccessTime.HasValue)
                            dirInfo.LastAccessTime = lastAccessTime.Value;
                        if (lastWriteTime.HasValue)
                            dirInfo.LastWriteTime = lastWriteTime.Value;
                        return DokanResult.Success;
                    }

                    return DokanResult.FileNotFound;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SetFileTime error: {ex}");
                return DokanResult.Error;
            }
        }

        #endregion

        #region 文件和目录管理

        public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
        {
            Console.WriteLine($"Try Deleting {fileName}");
            try
            {
                if (!_fs.FileExists(fileName))
                    return DokanResult.FileNotFound;

                return DokanResult.Success;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DeleteFile error: {ex}");
                return DokanResult.Error;
            }
        }

        public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
        {
            try
            {
                lock (_fs)
                {
                    if (!_fs.DirectoryExists(fileName))
                        return DokanResult.PathNotFound;

                    // 检查目录是否为空
                    var dirInfo = _fs.GetDirectoryInfo(fileName);
                    if (dirInfo.GetFileSystemInfos().Count() > 0)
                        return DokanResult.DirectoryNotEmpty;

                    return DokanResult.Success;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DeleteDirectory error: {ex}");
                return DokanResult.Error;
            }
        }

        public NtStatus MoveFile(
            string oldName,
            string newName,
            bool replace,
            IDokanFileInfo info)
        {
            try
            {
                lock (_fs)
                {
                    if (_fs.FileExists(oldName))
                    {
                        if (_fs.FileExists(newName))
                        {
                            if (!replace)
                                return DokanResult.FileExists;
                            _fs.DeleteFile(newName);
                        }
                        _fs.MoveFile(oldName, newName);
                        return DokanResult.Success;
                    }

                    if (_fs.DirectoryExists(oldName))
                    {
                        if (_fs.DirectoryExists(newName))
                            return DokanResult.FileExists;
                        _fs.MoveDirectory(oldName, newName);
                        return DokanResult.Success;
                    }

                    return DokanResult.FileNotFound;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MoveFile error: {ex}");
                return DokanResult.Error;
            }
        }

        public NtStatus CreateDirectory(string fileName, IDokanFileInfo info)
        {
            try
            {
                lock (_fs)
                {
                    if (_fs.DirectoryExists(fileName))
                        return DokanResult.AlreadyExists;

                    _fs.CreateDirectory(fileName);
                    return DokanResult.Success;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CreateDirectory error: {ex}");
                return DokanResult.Error;
            }
        }

        #endregion

        #region 锁操作

        public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            // 我们这里不实现文件锁定
            return DokanResult.Success;
        }

        public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            // 我们这里不实现文件锁定
            return DokanResult.Success;
        }

        #endregion

        #region 磁盘信息

        public NtStatus GetDiskFreeSpace(
            out long freeBytesAvailable,
            out long totalNumberOfBytes,
            out long totalNumberOfFreeBytes,
            IDokanFileInfo info)
        {
            try
            {
                var size = _fs.Size;
                var used = _fs.UsedSpace;

                totalNumberOfBytes = size;
                totalNumberOfFreeBytes = size - used;
                freeBytesAvailable = totalNumberOfFreeBytes; // 简化处理

                return DokanResult.Success;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GetDiskFreeSpace error: {ex}");
                freeBytesAvailable = 0;
                totalNumberOfBytes = 0;
                totalNumberOfFreeBytes = 0;
                return DokanResult.Error;
            }
        }

        public NtStatus GetVolumeInformation(
            out string volumeLabel,
            out FileSystemFeatures features,
            out string fileSystemName,
            out uint maximumComponentLength,
            IDokanFileInfo info)
        {
            volumeLabel = "VHDXFS";
            features = FileSystemFeatures.CasePreservedNames |
                      FileSystemFeatures.CaseSensitiveSearch |
                      FileSystemFeatures.PersistentAcls |
                      FileSystemFeatures.SupportsRemoteStorage |
                      FileSystemFeatures.UnicodeOnDisk;
            fileSystemName = "NTFS";
            maximumComponentLength = 255;
            return DokanResult.Success;
        }

        #endregion

        #region 挂载/卸载

        public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
        {
            Console.WriteLine($"Mounted at {mountPoint}");
            return DokanResult.Success;
        }

        public NtStatus Unmounted(IDokanFileInfo info)
        {
            _fs.Dispose();
            _container.Dispose();
            Console.WriteLine("Unmounted");
            return DokanResult.Success;
        }

        NtStatus IDokanOperations.FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info)
        {
            files = [];
            return DokanResult.NotImplemented;
        }

        NtStatus IDokanOperations.GetFileSecurity(string fileName, out FileSystemSecurity? security, AccessControlSections sections, IDokanFileInfo info)
        {
            security = null;
            return DokanResult.NotImplemented;
        }

        NtStatus IDokanOperations.SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
        {
            return NtStatus.NotImplemented;
        }

        #endregion
    }

    class WrappedVhdDokanFSHelper
    {
        public static ManualResetEventSlim CreateOrMountAsync(string mountPoint, string containerPath, string password, long diskSize)
        {
            // Use ManualResetEventSlim for better performance in this scenario
            var unmountSignal = new ManualResetEventSlim(false);

            // Start a new thread for mounting
            var mountThread = new Thread(() =>
            {
                try
                {
                    if (mountPoint.EndsWith("\\"))
                    {
                        mountPoint = mountPoint.Substring(0, mountPoint.Length - 1);
                    }

                    if (!Directory.Exists(mountPoint))
                    {
                        Directory.CreateDirectory(mountPoint);
                    }

                    File.SetAttributes(mountPoint, FileAttributes.Hidden);

                    var dokanLogger = new NullLogger();
                    using var dokan = new Dokan(dokanLogger);

                    // Also handle Ctrl+C in this thread
                    Console.CancelKeyPress += (object? sender, ConsoleCancelEventArgs e) =>
                    {
                        e.Cancel = true;
                        unmountSignal.Set();
                    };

                    var efs = new WrappedVhdDokanFS(containerPath, password, diskSize);
                    var dokanBuilder = new DokanInstanceBuilder(dokan)
                        .ConfigureOptions(options =>
                        {
                            options.Options = DokanOptions.FixedDrive;
                            options.MountPoint = mountPoint;
                        });

                    using (var dokanInstance = dokanBuilder.Build(efs))
                    {
                        Utils.TryOpenFolder(mountPoint);
                        unmountSignal.Wait();
                    }

                    Directory.Delete(mountPoint);
                    Console.WriteLine(@"Success");
                }
                catch (DokanException ex)
                {
                    Console.WriteLine(@"Error: " + ex.Message);
                }
            });

            // Configure and start the thread
            mountThread.IsBackground = true; // So it won't prevent process exit
            mountThread.Start();

            // Return the signal that can be used to trigger unmounting
            return unmountSignal;
        }
        public static void CreateOrMount(string mountPoint, string containerPath, string password, long diskSize)
        {
            try
            {
                if (mountPoint.EndsWith("\\"))
                {
                    mountPoint = mountPoint.Substring(0, mountPoint.Length - 1);
                }

                if (!Directory.Exists(mountPoint))
                {
                    Directory.CreateDirectory(mountPoint);
                }

                using var mre = new System.Threading.ManualResetEvent(false);
                using var dokanLogger = new ConsoleLogger("[Dokan] ");
                using var dokan = new Dokan(dokanLogger);
                Console.CancelKeyPress += (object? sender, ConsoleCancelEventArgs e) =>
                {
                    e.Cancel = true;
                    mre.Set();
                };

                var efs = new WrappedVhdDokanFS(containerPath, password, diskSize);
                var dokanBuilder = new DokanInstanceBuilder(dokan)
                    .ConfigureOptions(options =>
                    {
                        options.Options = DokanOptions.StderrOutput;
                        options.MountPoint = mountPoint;
                    });
                using (var dokanInstance = dokanBuilder.Build(efs))
                {
                    mre.WaitOne();
                }

                Directory.Delete(mountPoint);

                Console.WriteLine(@"Success");
            }
            catch (DokanException ex)
            {
                Console.WriteLine(@"Error: " + ex.Message);
            }
        }
        public static void CopyCompacted(string pathOrigin, string pathDest, string password, long newDiskSize = 100L * 1024 * 1024 * 1024,
            ProgressCallback progressCallback = null)
        {
            using (var originStream = new FileStream(pathOrigin, FileMode.Open, FileAccess.ReadWrite))
            {
                byte[] encryptedKey = new byte[96];
                byte[] originalHeader = new byte[WrappedVhdDokanFS.headerReservedSize];
                originStream.Read(originalHeader, 0, originalHeader.Length);
                Array.Copy(originalHeader, encryptedKey, 96);
                byte[] key = StringPasswordEncryption.Decrypt(encryptedKey, password);

                OffsetStream offsetStreamOrigin = new OffsetStream(originStream, WrappedVhdDokanFS.headerReservedSize);

                EncryptedStreamOpt encStreamOrigin = new EncryptedStreamOpt(offsetStreamOrigin, key);

                var diskOrigin = new Disk(encStreamOrigin, Ownership.None);
                VolumeManager volumeManagerOrigin = new VolumeManager(diskOrigin);
                VolumeInfo volumeOrigin = volumeManagerOrigin.GetLogicalVolumes()[0];
                NtfsFileSystem oFs = new NtfsFileSystem(volumeOrigin.Open());

                // 创建文件并预留4K空间
                using (var fs = new FileStream(pathDest, FileMode.Create, FileAccess.ReadWrite))
                {
                    fs.Write(originalHeader, 0, originalHeader.Length);
                    fs.Flush();

                    // 使用DiscUtils创建VHDX
                    OffsetStream offsetStreamNew = new OffsetStream(fs, WrappedVhdDokanFS.headerReservedSize);

                    EncryptedStreamOpt encStreamNew = new EncryptedStreamOpt(offsetStreamNew, key);

                    // 创建VHDX磁盘
                    var diskNew = Disk.InitializeDynamic(encStreamNew, Ownership.None, newDiskSize);

                    BiosPartitionTable.Initialize(diskNew, WellKnownPartitionType.WindowsNtfs);
                    var volMgrNew = new VolumeManager(diskNew);
                    NtfsFileSystem nFs = NtfsFileSystem.Format(volMgrNew.GetLogicalVolumes()[0], null, new NtfsFormatOptions());

                    // 开始复制文件系统
                    var ntfsCopier = new NtfsCopier();
                    ntfsCopier.CopyFileSystem(oFs, nFs, progressCallback);
                }
            }
        }
        public static void CopyDirectoryToNewContainer(string sourcePath, string destinationPath, string password, long diskSize = 100L * 1024 * 1024 * 1024,
            ProgressCallback progressCallback = null)
        {
            WrappedVhdDokanFS.CreateContainer(destinationPath, password, diskSize);
            (var fileStream, var ntfsFs) = WrappedVhdDokanFS.LoadFs(destinationPath, password);
            try
            {
                CopyDirectoryToNtfs(sourcePath, destinationPath, ntfsFs, progressCallback);
            }
            finally
            {
                ntfsFs.Dispose();
                fileStream.Dispose();
            }
        }

        public static void CopyContaierToDirectory(string sourcePath, string destinationPath, string password,
            ProgressCallback progressCallback = null)
        {
            (var containerFs, var ntfs) = WrappedVhdDokanFS.LoadFs(sourcePath, password);
            try
            {
                NtfsFolderCopier.CopyNtfsToRealDirectory(ntfs, destinationPath, progressCallback);
            }
            finally
            {
                ntfs.Dispose();
                containerFs.Dispose();
            }
        }
    }

    class NtfsCopier
    {
        public long totalFilesCopied = 0;
        public long totalDirectoriesCopied = 0;
        public long totalSizeCopied = 0;
        public long totalSizeUsed = 0;
        public void CopyFileSystem(NtfsFileSystem source, NtfsFileSystem target, ProgressCallback progressCallback)
        {
            totalSizeUsed = source.UsedSpace;
            // 复制根目录内容
            CopyDirectory(source, "/", target, "/", progressCallback);
        }

        private void CopyDirectory(
            NtfsFileSystem source, string sourcePath,
            NtfsFileSystem target, string targetPath,
            ProgressCallback progressCallback)
        {
            // 创建目标目录（如果不存在）
            if (!target.DirectoryExists(targetPath))
            {
                try
                {
                    target.CreateDirectory(targetPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error creating directory: {ex.Message}");
                }
            }

            // 关键修改：获取文件列表快照
            var files = source.GetFiles(sourcePath).ToArray();  // 转换为数组
            foreach (var file in files)
            {
                progressCallback?.Invoke(totalSizeCopied, totalSizeUsed, file);

                var sourceFilePath = Path.Combine(sourcePath, file);
                var targetFilePath = Path.Combine(targetPath, file);
                CopyFile(source, sourceFilePath, target, targetFilePath, progressCallback);
            }

            // 关键修改：获取目录列表快照
            var dirs = source.GetDirectories(sourcePath).ToArray();  // 转换为数组
            foreach (var dir in dirs)
            {
                progressCallback?.Invoke(totalSizeCopied, totalSizeUsed, dir);

                var sourceDirPath = Path.Combine(sourcePath, dir);
                var targetDirPath = Path.Combine(targetPath, dir);
                CopyDirectory(source, sourceDirPath, target, targetDirPath, progressCallback);
            }

            totalDirectoriesCopied++;
        }

        private void CopyFile(
            NtfsFileSystem source, string sourcePath,
            NtfsFileSystem target, string targetPath,
            ProgressCallback progressCallback)
        {
            try
            {
                // 创建目标目录结构（确保父目录存在）
                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir) && !target.DirectoryExists(targetDir))
                {
                    target.CreateDirectory(targetDir);
                }

                // 复制文件内容
                using (Stream sourceStream = source.OpenFile(sourcePath, FileMode.Open))
                using (Stream targetStream = target.OpenFile(targetPath, FileMode.Create))
                {
                    byte[] buffer = new byte[81920]; // 80KB缓冲区
                    int bytesRead;

                    while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        targetStream.Write(buffer, 0, bytesRead);
                        totalSizeCopied += bytesRead;

                        // 更新进度
                        progressCallback?.Invoke(totalSizeCopied, totalSizeUsed, sourcePath);
                    }
                }

                // 复制文件属性
                var sourceInfo = source.GetFileInfo(sourcePath);
                var targetInfo = target.GetFileInfo(targetPath);

                // 复制基础属性
                targetInfo.CreationTime = sourceInfo.CreationTime;
                targetInfo.LastAccessTime = sourceInfo.LastAccessTime;
                targetInfo.LastWriteTime = sourceInfo.LastWriteTime;
                targetInfo.Attributes = sourceInfo.Attributes;

                totalFilesCopied++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error copying file: {ex.Message}");
            }
        }
    }

    class FolderNtfsCopier
    {
        public delegate void ProgressCallback(long bytesCopied, long totalBytes, string currentFile);

        public static void CopyDirectoryToNtfs(
            string sourceDirectory,
            string containerPath,
            NtfsFileSystem ntfs,
            ProgressCallback progressCallback = null)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");
            }

            // 1. 计算源文件夹总大小
            long totalSize = CalculateDirectorySize(sourceDirectory);
            Console.WriteLine($"Total size to copy: {totalSize / 1024 / 1024} MB");

            // 2. 检查目标NTFS卷是否有足够空间(保留100MB余量)
            long requiredSpace = totalSize + (100 * 1024 * 1024); // 总大小 + 100MB
            if (!HasEnoughDiskSpace(containerPath, requiredSpace))
            {
                throw new IOException($"Insufficient space on NTFS volume. Required: {requiredSpace / 1024 / 1024} MB");
            }

            // 3. 开始复制
            DirectoryInfo sourceDir = new DirectoryInfo(sourceDirectory);
            long bytesCopied = 0;

            CopyDirectoryContents(sourceDir, "", ntfs, ref bytesCopied, totalSize, progressCallback);
        }

        private static long CalculateDirectorySize(string directory)
        {
            long size = 0;
            var dirInfo = new DirectoryInfo(directory);

            // 添加所有文件大小
            foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
            {
                size += file.Length;
            }

            return size;
        }

        public static bool HasEnoughDiskSpace(string filePath, long fileSize)
        {
            try
            {
                // 获取文件所在驱动器的信息
                var driveInfo = GetDriveInfoForPath(filePath);

                if (driveInfo == null)
                {
                    Console.WriteLine("无法确定文件所在驱动器。");
                    return false;
                }

                // 计算可用空间（减去缓冲）
                long availableSpace = driveInfo.AvailableFreeSpace;

                // 检查空间是否足够
                bool hasEnoughSpace = availableSpace >= fileSize;

                if (!hasEnoughSpace)
                {
                    throw new IOException($"Insufficient space on NTFS volume. Required: {fileSize / 1024 / 1024} MB");
                }

                return hasEnoughSpace;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"检查磁盘空间时出错: {ex.Message}");
                return false;
            }
        }

        private static DriveInfo GetDriveInfoForPath(string filePath)
        {
            // 获取文件所在驱动器的根目录（如"C:\"）
            string root = Path.GetPathRoot(Path.GetFullPath(filePath));

            // 获取所有驱动器信息
            DriveInfo[] allDrives = DriveInfo.GetDrives();

            // 查找匹配的驱动器
            foreach (DriveInfo drive in allDrives)
            {
                if (string.Equals(drive.Name, root, StringComparison.OrdinalIgnoreCase))
                {
                    return drive;
                }
            }

            return null;
        }

        private static void CopyDirectoryContents(
            DirectoryInfo sourceDir,
            string relativePath,
            NtfsFileSystem ntfs,
            ref long bytesCopied,
            long totalBytes,
            ProgressCallback progressCallback)
        {
            // 复制所有文件
            foreach (FileInfo file in sourceDir.GetFiles())
            {
                string destPath = Path.Combine(relativePath, file.Name);

                // 通知开始复制新文件
                progressCallback?.Invoke(bytesCopied, totalBytes, file.FullName);

                // 使用文件流逐块复制，支持大文件
                using (FileStream sourceStream = file.OpenRead())
                using (Stream ntfsFileStream = ntfs.OpenFile(destPath, FileMode.Create, FileAccess.Write))
                {
                    byte[] buffer = new byte[81920]; // 80KB缓冲区
                    int bytesRead;

                    while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ntfsFileStream.Write(buffer, 0, bytesRead);
                        bytesCopied += bytesRead;

                        // 更新进度
                        progressCallback?.Invoke(bytesCopied, totalBytes, file.FullName);
                    }
                }

                // 复制文件属性
                ntfs.SetAttributes(destPath, file.Attributes);

            }

            // 递归复制所有子目录
            foreach (DirectoryInfo subDir in sourceDir.GetDirectories())
            {
                string newRelativePath = Path.Combine(relativePath, subDir.Name);

                ntfs.CreateDirectory(newRelativePath);

                // 更新进度(目录创建也算进度)
                progressCallback?.Invoke(bytesCopied, totalBytes, subDir.FullName);

                CopyDirectoryContents(subDir, newRelativePath, ntfs, ref bytesCopied, totalBytes, progressCallback);
                
                // 复制目录属性
                ntfs.SetAttributes(newRelativePath, subDir.Attributes);
            }
        }
    }

    class NtfsFolderCopier
    {
        public static void CopyNtfsToRealDirectory(NtfsFileSystem ntfs, string outputPath, ProgressCallback progressCallback)
        {
            // 确保输出目录存在
            Directory.CreateDirectory(outputPath);

            // 计算NTFS文件系统的总字节数
            long totalBytes = CalculateTotalSize(ntfs, "\\");
            long totalCopied = 0;

            // 创建目录栈进行非递归遍历
            var stack = new Stack<(string Source, string Destination)>();
            stack.Push(("\\", outputPath));

            while (stack.Count > 0)
            {
                var (sourceDir, destDir) = stack.Pop();

                // 在目标位置创建目录
                Directory.CreateDirectory(destDir);

                // 复制当前目录下的所有文件
                var files = ntfs.GetFiles(sourceDir).ToArray();  // 转换为数组
                foreach (var fileName in files)
                {
                    var relfileName = fileName.TrimStart('\\', '/');
                    string sourceFilePath = Path.Combine(sourceDir, fileName);
                    string destFilePath = Path.Combine(outputPath, relfileName);

                    CopyFileWithProgress(
                        ntfs,
                        sourceFilePath,
                        destFilePath,
                        ref totalCopied,
                        totalBytes,
                        progressCallback
                    );
                }

                // 处理子目录（反向入栈以保持原始顺序）
                var subDirs = new List<string>(ntfs.GetDirectories(sourceDir));
                subDirs.Reverse(); // 反转顺序以保持原始目录顺序
                foreach (var subDir in subDirs)
                {
                    var relSubDir = subDir.TrimStart('\\', '/');
                    string sourceSubDir = Path.Combine(sourceDir, subDir);
                    string destSubDir = Path.Combine(outputPath, relSubDir);
                    stack.Push((sourceSubDir, destSubDir));
                }
            }
        }

        private static long CalculateTotalSize(NtfsFileSystem ntfs, string path)
        {
            long size = 0;

            // 累加文件大小
            foreach (var file in ntfs.GetFiles(path))
            {
                string filePath = Path.Combine(path, file);
                size += ntfs.GetFileInfo(filePath).Length;
            }

            // 递归处理子目录
            foreach (var dir in ntfs.GetDirectories(path))
            {
                string subDir = Path.Combine(path, dir);
                size += CalculateTotalSize(ntfs, subDir);
            }

            return size;
        }

        private static void CopyFileWithProgress(
            NtfsFileSystem ntfs,
            string sourcePath,
            string destPath,
            ref long totalCopied,
            long totalBytes,
            ProgressCallback progressCallback)
        {
            using (Stream source = ntfs.OpenFile(sourcePath, FileMode.Open))
            using (FileStream dest = new FileStream(destPath, FileMode.Create))
            {
                byte[] buffer = new byte[1024 * 1024]; // 1MB缓冲区
                int bytesRead;
                long fileCopied = 0;
                long fileLength = source.Length;

                while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    dest.Write(buffer, 0, bytesRead);

                    // 更新进度
                    fileCopied += bytesRead;
                    totalCopied += bytesRead;
                    progressCallback?.Invoke(totalCopied, totalBytes, sourcePath);
                }
            }
        }
    }
}
