using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LegendBorn.Models;

public sealed class FriendsListResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }

    [JsonPropertyName("friends")] public List<FriendDto> Friends { get; set; } = new();
}

public sealed class FriendDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("publicId")] public string? PublicId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("image")] public string? Image { get; set; }

    // если на сайте решишь отдать status/lastSeen — просто добавишь тут
    [JsonPropertyName("status")] public string? Status { get; set; }
}

public sealed class ApiOkResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}