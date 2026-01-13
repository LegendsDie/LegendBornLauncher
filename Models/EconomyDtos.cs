namespace LegendBorn.Models;

public sealed class RezoniteBalanceResponse
{
    public string Currency { get; set; } = "RZN";
    public long Balance { get; set; }

    public string SafeCurrency
    {
        get
        {
            var c = (Currency ?? "").Trim();
            return string.IsNullOrWhiteSpace(c) ? "RZN" : c;
        }
    }
}

public sealed class LauncherEventRequest
{
    // например: "launcher_open"
    public string Key { get; set; } = "";

    // обязателен!
    public string IdempotencyKey { get; set; } = "";

    // любые данные (опционально)
    public object? Payload { get; set; }

    public string SafeKey => (Key ?? "").Trim();
    public string SafeIdempotencyKey => (IdempotencyKey ?? "").Trim();

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(SafeKey) &&
        !string.IsNullOrWhiteSpace(SafeIdempotencyKey);
}

public sealed class LauncherEventResponse
{
    public bool Ok { get; set; }
    public bool Rewarded { get; set; }      // сервер сказал: было начисление или нет
    public long Balance { get; set; }       // актуальный баланс после обработки
    public string? Message { get; set; }

    public string SafeMessage => (Message ?? "").Trim();
}