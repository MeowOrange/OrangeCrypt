using System.Security.Cryptography;

namespace OrangeCrypt.EncryptionImpl
{
    public class PasswordHasher
    {
        // 参数定义
        private const int SaltSize = 32;     // 256位盐值 (32字节)
        private const int Iterations = 1000; // 迭代次数
        private const int HashSize = 32;      // 256位哈希值 (32字节)

        // 计算最终哈希值的总长度: SaltSize + HashSize
        public const int HashedPasswordLength = SaltSize + HashSize; // 64字节

        public static byte[] HashPassword(string password)
        {
            // 生成随机盐值 (32字节)
            byte[] salt = new byte[SaltSize];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }

            // 使用PBKDF2派生密钥 (32字节哈希)
            using (var pbkdf2 = new Rfc2898DeriveBytes(
                password,
                salt,
                Iterations,
                HashAlgorithmName.SHA256))
            {
                byte[] hash = pbkdf2.GetBytes(HashSize);

                // 组合盐值和哈希值 (32 + 32 = 64字节)
                byte[] hashedPassword = new byte[HashedPasswordLength];
                Buffer.BlockCopy(salt, 0, hashedPassword, 0, SaltSize);
                Buffer.BlockCopy(hash, 0, hashedPassword, SaltSize, HashSize);

                return hashedPassword;
            }
        }

        public static bool VerifyPassword(string password, byte[] hashedPassword)
        {
            // 验证输入长度是否正确
            if (hashedPassword == null || hashedPassword.Length != HashedPasswordLength)
            {
                return false;
            }

            // 提取盐值 (前32字节)
            byte[] salt = new byte[SaltSize];
            Buffer.BlockCopy(hashedPassword, 0, salt, 0, SaltSize);

            // 提取存储的哈希值 (后32字节)
            byte[] storedHash = new byte[HashSize];
            Buffer.BlockCopy(hashedPassword, SaltSize, storedHash, 0, HashSize);

            // 计算输入密码的哈希值
            using (var pbkdf2 = new Rfc2898DeriveBytes(
                password,
                salt,
                Iterations,
                HashAlgorithmName.SHA256))
            {
                byte[] computedHash = pbkdf2.GetBytes(HashSize);

                // 比较哈希值 (使用恒定时间比较防止时序攻击)
                return CryptographicOperations.FixedTimeEquals(computedHash, storedHash);
            }
        }
    }
}
