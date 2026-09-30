using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace App1.Core
{
    /// <summary>
    /// 凭据本地加密存储：使用机器相关信息派生密钥的 AES-GCM 保护（避免明文存储令牌）。
    /// 注意：这是本地防窥探措施，不能替代服务端安全。
    /// </summary>
    public static class TokenProtector
    {
        private static readonly byte[] Salt =
        {
            0x4e, 0x6f, 0x76, 0x61, 0x4c, 0x61, 0x75, 0x6e, 0x63, 0x68, 0x65, 0x72, 0x21, 0x40, 0x23, 0x24
        };

        private static byte[] Key { get; } = DeriveKey();

        private static byte[] DeriveKey()
        {
            var seed = $"{Environment.MachineName}|{Environment.UserName}|{Environment.ProcessorCount}";
            using var pbkdf2 = new Rfc2898DeriveBytes(seed, Salt, 100_000, HashAlgorithmName.SHA256);
            return pbkdf2.GetBytes(32);
        }

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return string.Empty;
            var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
            RandomNumberGenerator.Fill(nonce);
            var plaintext = Encoding.UTF8.GetBytes(plain);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[AesGcm.TagByteSizes.MaxSize];
            using var gcm = new AesGcm(Key, AesGcm.TagByteSizes.MaxSize);
            gcm.Encrypt(nonce, plaintext, ciphertext, tag);

            using var ms = new MemoryStream();
            ms.WriteByte(1);
            ms.Write(nonce);
            ms.Write(tag);
            ms.Write(ciphertext);
            return Convert.ToBase64String(ms.ToArray());
        }

        public static string Unprotect(string? protectedText)
        {
            if (string.IsNullOrEmpty(protectedText)) return string.Empty;
            try
            {
                var data = Convert.FromBase64String(protectedText);
                if (data.Length < 2 || data[0] != 1)
                {
                    // 兼容历史明文数据
                    return protectedText;
                }
                using var ms = new MemoryStream(data);
                ms.ReadByte();
                var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
                var tag = new byte[AesGcm.TagByteSizes.MaxSize];
                ms.ReadExactly(nonce, 0, nonce.Length);
                ms.ReadExactly(tag, 0, tag.Length);
                var ciphertext = new byte[data.Length - 1 - nonce.Length - tag.Length];
                ms.ReadExactly(ciphertext, 0, ciphertext.Length);

                var plaintext = new byte[ciphertext.Length];
                using var gcm = new AesGcm(Key, AesGcm.TagByteSizes.MaxSize);
                gcm.Decrypt(nonce, ciphertext, tag, plaintext);
                return Encoding.UTF8.GetString(plaintext);
            }
            catch (Exception ex)
            {
                Logger.Warn("凭据解密失败：" + ex.Message);
                return string.Empty;
            }
        }
    }
}
