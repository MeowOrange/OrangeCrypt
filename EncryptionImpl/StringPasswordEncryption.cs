using System.IO;
using System.Security.Cryptography;

namespace OrangeCrypt
{
    internal class StringPasswordEncryption
    {
        // 加密 byte[] 数据
        public static byte[] Encrypt(byte[] data, string password)
        {
            // 参数检查
            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be empty", nameof(data));
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password cannot be empty", nameof(password));

            using (Aes aes = Aes.Create())
            {
                // 使用 PBKDF2 从密码派生密钥和 IV
                var salt = GenerateRandomBytes(16); // 16字节的随机盐值
                var derivedBytes = new Rfc2898DeriveBytes(password, salt, 10000, HashAlgorithmName.SHA256);
                aes.Key = derivedBytes.GetBytes(32); // 256位密钥
                aes.IV = derivedBytes.GetBytes(16);  // 128位IV

                using (var memoryStream = new MemoryStream())
                {
                    memoryStream.Write(salt, 0, salt.Length); // 将盐值写入输出流开头

                    using (var cryptoStream = new CryptoStream(memoryStream, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        cryptoStream.Write(data, 0, data.Length);
                        cryptoStream.FlushFinalBlock();
                    }

                    return memoryStream.ToArray();
                }
            }
        }

        // 解密 byte[] 数据
        public static byte[] Decrypt(byte[] encryptedData, string password)
        {
            // 参数检查
            if (encryptedData == null || encryptedData.Length == 0)
                throw new ArgumentException("Encrypted data cannot be empty", nameof(encryptedData));
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password cannot be empty", nameof(password));

            using (Aes aes = Aes.Create())
            {
                // 从加密数据中读取盐值
                byte[] salt = new byte[16];
                Array.Copy(encryptedData, 0, salt, 0, salt.Length);

                // 使用相同的 PBKDF2 参数派生密钥和 IV
                var derivedBytes = new Rfc2898DeriveBytes(password, salt, 10000, HashAlgorithmName.SHA256);
                aes.Key = derivedBytes.GetBytes(32); // 256位密钥
                aes.IV = derivedBytes.GetBytes(16);  // 128位IV

                using (var memoryStream = new MemoryStream())
                {
                    using (var cryptoStream = new CryptoStream(memoryStream, aes.CreateDecryptor(), CryptoStreamMode.Write))
                    {
                        // 跳过盐值部分(前16字节)
                        cryptoStream.Write(encryptedData, salt.Length, encryptedData.Length - salt.Length);
                    }

                    return memoryStream.ToArray();
                }
            }
        }

        // 生成随机字节数组
        private static byte[] GenerateRandomBytes(int length)
        {
            byte[] randomBytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(randomBytes);
            }
            return randomBytes;
        }
    }
}
