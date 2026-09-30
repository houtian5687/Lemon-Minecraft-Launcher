using System;
using App1.Core;

namespace App1.Models
{
    /// <summary>
    /// 启动器账户：支持微软正版与离线模式。
    /// </summary>
    public sealed class Account : ObservableObject
    {
        private bool _isActive;
        private string _playerName = "";

        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public AccountType Type { get; set; } = AccountType.Offline;

        public string PlayerName
        {
            get => _playerName;
            set => Set(ref _playerName, value);
        }

        public string Uuid { get; set; } = "";

        /// <summary>加密存储的访问令牌。</summary>
        public string? EncryptedAccessToken { get; set; }

        /// <summary>加密存储的刷新令牌（微软账户）。</summary>
        public string? EncryptedRefreshToken { get; set; }

        public DateTimeOffset? ExpiresAt { get; set; }

        public string? SkinUrl { get; set; }

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (Set(ref _isActive, value)) Raise(nameof(StatusText));
            }
        }

        /// <summary>是否已拥有游戏（正版校验）。</summary>
        public bool OwnsGame { get; set; }

        public string AccessToken
        {
            get => TokenProtector.Unprotect(EncryptedAccessToken);
            set => EncryptedAccessToken = TokenProtector.Protect(value);
        }

        public string RefreshToken
        {
            get => TokenProtector.Unprotect(EncryptedRefreshToken);
            set => EncryptedRefreshToken = TokenProtector.Protect(value);
        }

        public bool IsExpired => Type == AccountType.Microsoft && ExpiresAt is not null && ExpiresAt < DateTimeOffset.Now.AddMinutes(-1);

        public string StatusText => Type switch
        {
            AccountType.Microsoft when IsExpired => "需要刷新",
            AccountType.Microsoft => OwnsGame ? "正版已验证" : "未检出游戏授权",
            _ => "离线账户"
        };

        public string TypeText => Type.ToText();

        public string Initial => string.IsNullOrEmpty(PlayerName) ? "?" : PlayerName[..1].ToUpperInvariant();
    }

    /// <summary>
    /// 微软设备代码登录会话。
    /// </summary>
    public sealed class DeviceCodeSession
    {
        public string DeviceCode { get; set; } = "";
        public string UserCode { get; set; } = "";
        public string VerificationUri { get; set; } = "";
        public int ExpiresIn { get; set; }
        public int Interval { get; set; } = 5;
        public string Message { get; set; } = "";
    }
}
