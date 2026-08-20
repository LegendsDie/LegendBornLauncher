// File: ViewModels/MainViewModel.MinecraftIdentity.cs
using System;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>
    /// Resolves the technical Minecraft username used to build the offline GameProfile.
    /// A Minecraft-compatible serverNick is authoritative because the dedicated server keys
    /// vanilla playerdata by the offline UUID derived from this exact launch name.
    /// Display-only nicknames that contain spaces/Unicode remain aliases and safely fall back
    /// to the linked Minecraft username.
    /// </summary>
    private string ResolveLaunchMinecraftUsername()
    {
        static string Valid(string? value)
        {
            var candidate = (value ?? string.Empty).Trim();
            return IsValidMcName(candidate) ? candidate : string.Empty;
        }

        var profile = Profile;

        var candidate = Valid(profile?.Minecraft?.ServerNick);
        if (candidate.Length > 0) return candidate;

        candidate = Valid(profile?.ServerNick);
        if (candidate.Length > 0) return candidate;

        candidate = Valid(profile?.Minecraft?.EffectiveServerNick);
        if (candidate.Length > 0) return candidate;

        candidate = Valid(profile?.Minecraft?.Username);
        if (candidate.Length > 0) return candidate;

        candidate = Valid(profile?.MinecraftName);
        if (candidate.Length > 0) return candidate;

        // During a fresh login the current site identity must outrank any local value left by
        // another account/session. Local Username/LastUsername are recovery-only fallbacks.
        candidate = Valid(profile?.UserName);
        if (candidate.Length > 0) return candidate;

        candidate = Valid(SiteUserName);
        if (candidate.Length > 0) return candidate;

        candidate = Valid(Username);
        if (candidate.Length > 0) return candidate;

        try
        {
            candidate = Valid(_config.Current.LastUsername);
            if (candidate.Length > 0) return candidate;
        }
        catch
        {
        }

        return "Player";
    }
}
