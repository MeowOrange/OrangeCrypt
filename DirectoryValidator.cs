using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public class DirectoryValidator
{
    private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ".", ".."
    };

    public static string ValidateDirectory(string path)
    {
        // 1. 检查路径是否存在且是目录
        if (!Directory.Exists(path))
            return $"Directory does not exist: {path}";

        try
        {
            string fullPath = Path.GetFullPath(path);

            // 2. 检查是否是根目录
            string root = Path.GetPathRoot(fullPath);
            if (string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            {
                return $"Path is a root directory: {path}";
            }

            // 3. 检查UNC路径（直接网络共享）
            if (IsUncPath(fullPath))
                return $"Path is a network share (UNC path): {path}";

            // 4. 递归检查目录树
            Stack<string> stack = new Stack<string>();
            stack.Push(fullPath);

            while (stack.Count > 0)
            {
                string currentDir = stack.Pop();

                // 检查当前目录属性
                var dirAttrs = File.GetAttributes(currentDir);
                if ((dirAttrs & FileAttributes.ReparsePoint) != 0)
                    return $"Directory is a reparse point: {currentDir}";

                // 检查目录名
                string dirName = Path.GetFileName(currentDir);
                if (ReservedNames.Contains(dirName))
                    return $"Directory has reserved name: {currentDir}";

                try
                {
                    // 处理文件
                    foreach (string file in Directory.GetFiles(currentDir))
                    {
                        string fileName = Path.GetFileName(file);
                        string baseName = Path.GetFileNameWithoutExtension(fileName);

                        if (ReservedNames.Contains(fileName)) // 检查完整文件名
                            return $"File has reserved name: {file}";
                        if (ReservedNames.Contains(baseName)) // 检查无扩展名部分
                            return $"File has reserved base name: {file}";

                        var fileAttrs = File.GetAttributes(file);
                        if ((fileAttrs & FileAttributes.ReparsePoint) != 0)
                            return $"File is a reparse point: {file}";
                    }

                    // 处理子目录
                    foreach (string subDir in Directory.GetDirectories(currentDir))
                    {
                        var subDirAttrs = File.GetAttributes(subDir);
                        string subDirName = Path.GetFileName(subDir);

                        // 立即检查子目录名
                        if (ReservedNames.Contains(subDirName))
                            return $"Subdirectory has reserved name: {subDir}";

                        // 立即检查重解析点
                        if ((subDirAttrs & FileAttributes.ReparsePoint) != 0)
                            return $"Subdirectory is a reparse point: {subDir}";

                        stack.Push(subDir);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    return $"Access denied in directory: {currentDir}";
                }
                catch (PathTooLongException)
                {
                    return $"Path too long: {currentDir}";
                }
                catch (DirectoryNotFoundException)
                {
                    return $"Subdirectory not found in: {currentDir}";
                }
            }

            return null; // 所有检查通过
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
        {
            return $"Invalid path format: {ex.Message}";
        }
    }

    private static bool IsUncPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        // 检查路径是否以"\\"开头
        bool isUnc = path.StartsWith(@"\\", StringComparison.Ordinal);

        // 进一步验证UNC格式
        if (isUnc)
        {
            try
            {
                Uri uri = new Uri(path);
                return uri.IsUnc;
            }
            catch
            {
                return true; // 如果无法解析为URI但仍以"\\"开头，则视为UNC
            }
        }

        return false;
    }
}