namespace LegendBorn.Models;

public sealed class RezoniteBalanceResponse
{
    public string Currency { get; set; } = "RZN";
    public long Balance { get; set; }
}

public sealed class LauncherEventRequest
{
    public string Key { get; set; } = "";              // например: "launcher_open"
    public string IdempotencyKey { get; set; } = "";   // обязателен!
    public object? Payload { get; set; }               // любые данные (опционально)
}

public sealed class LauncherEventResponse
{
    public bool Ok { get; set; }
    public bool Rewarded { get; set; }      // сервер сказал: было начисление или нет
    public long Balance { get; set; }       // актуальный баланс после обработки
    public string? Message { get; set; }
}