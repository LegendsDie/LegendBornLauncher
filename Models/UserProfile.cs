using System;

namespace LegendBorn.Models;

public sealed class UserProfile
{
    public string Id { get; set; } = "";
    public int? PublicId { get; set; }
    public string Role { get; set; } = "USER";
    public string UserName { get; set; } = "Unknown";
    public string? MinecraftName { get; set; }
    public string? ServerNick { get; set; }
    public string? AvatarUrl { get; set; }
    public string? BannerImage { get; set; }
    public string? ProfileThemeKey { get; set; }
    public string? FeaturedAchievements { get; set; }
    public long Rezonite { get; set; }
    public bool CanPlay { get; set; } = true;
    public string? Reason { get; set; }

    // /api/launcher/me already exposes these snapshots. Modeling them here prevents the launcher
    // from throwing away useful social/profile state and keeps display data on the server as source of truth.
    public MinecraftSnapshot? Minecraft { get; set; }
    public MinecraftStatusSnapshot? MinecraftStatus { get; set; }
    public ClanSnapshot? Clan { get; set; }
    public ProgressionSnapshot? Progression { get; set; }
    public SocialSnapshot? Social { get; set; }

    public sealed class MinecraftSnapshot
    {
        public string? Uuid { get; set; }
        public string? Username { get; set; }
        public string? ServerNick { get; set; }
        public string? EffectiveServerNick { get; set; }
        public bool IsLinked { get; set; }
        public string? SelectedSkinKey { get; set; }
        public SkinSnapshot? SelectedSkin { get; set; }
    }

    public sealed class MinecraftStatusSnapshot
    {
        public bool Online { get; set; }
        public string? ServerId { get; set; }
        public DateTimeOffset? SeenAt { get; set; }
        public DateTimeOffset? SessionStartedAt { get; set; }
        public string? Dimension { get; set; }
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? Z { get; set; }
        public double? Health { get; set; }
        public double? MaxHealth { get; set; }
        public double? Food { get; set; }
        public double? ExperienceLevel { get; set; }
        public double? ExperienceProgress { get; set; }
    }

    public sealed class SkinSnapshot
    {
        public string? Title { get; set; }
        public string? PreviewUrl { get; set; }
        public string? SkinUrl { get; set; }
        public bool IsEnabled { get; set; }
    }

    public sealed class ClanSnapshot
    {
        public string? Key { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? EmblemUrl { get; set; }
        public string? ColorHex { get; set; }
        public ClanRankSnapshot? Rank { get; set; }
        public DateTimeOffset? JoinedAt { get; set; }
    }

    public sealed class ClanRankSnapshot
    {
        public string? Key { get; set; }
        public string? Name { get; set; }
        public int Level { get; set; }
        public bool IsLeader { get; set; }
    }

    public sealed class ProgressionSnapshot
    {
        public long XpTotal { get; set; }
        public long XpSeason { get; set; }
        public int Level { get; set; } = 1;
        public long XpIntoLevel { get; set; }
        public long XpForNext { get; set; }
        public double XpProgress { get; set; }
    }

    public sealed class SocialSnapshot
    {
        public int FriendsCount { get; set; }
        public int PendingFriendRequests { get; set; }
        public int UnreadNotifications { get; set; }
    }

    public string SafeId => (Id ?? "").Trim();

    public string SafeRole
    {
        get
        {
            var r = (Role ?? "").Trim();
            return string.IsNullOrWhiteSpace(r) ? "USER" : r;
        }
    }

    public string SafeUserName
    {
        get
        {
            var n = (UserName ?? "").Trim();
            return string.IsNullOrWhiteSpace(n) ? "Unknown" : n;
        }
    }

    public string? SafeMinecraftName
    {
        get
        {
            var n = (MinecraftName ?? Minecraft?.Username ?? "").Trim();
            return string.IsNullOrWhiteSpace(n) ? null : n;
        }
    }

    public string? SafeAvatarUrl
    {
        get
        {
            var u = (AvatarUrl ?? "").Trim();
            return string.IsNullOrWhiteSpace(u) ? null : u;
        }
    }

    public string? SafeBannerImage
    {
        get
        {
            var u = (BannerImage ?? "").Trim();
            return string.IsNullOrWhiteSpace(u) ? null : u;
        }
    }

    public bool HasAvatar => SafeAvatarUrl is not null;
    public bool HasBanner => SafeBannerImage is not null;
    public string DisplayName => SafeUserName;
    public string EffectiveMinecraftName => SafeMinecraftName ?? DisplayName;

    public string DenyReason
    {
        get
        {
            if (CanPlay) return "";
            var r = (Reason ?? "").Trim();
            return string.IsNullOrWhiteSpace(r) ? "Доступ к игре ограничен." : r;
        }
    }
}
