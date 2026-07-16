using System.Net.Http.Json;

namespace BankApi.Services;

public class EngineClient(HttpClient http)
{
    public record ValidationResult(bool Approved, decimal Fee, string Reason);

    private record EngineResponse(bool Approved, decimal Fee, string Reason);

    public async Task<ValidationResult> ValidateAsync(string transactionType, decimal amount, decimal currentBalance)
    {
        var response = await http.PostAsJsonAsync("/validate", new { transactionType, amount, currentBalance });
        var result = await response.Content.ReadFromJsonAsync<EngineResponse>()
            ?? throw new InvalidOperationException("Engine returned an empty response");
        return new ValidationResult(result.Approved, result.Fee, result.Reason);
    }

    private record AccrueResponse(decimal Interest);

    public async Task<decimal> AccrueAsync(string accountType, decimal balance)
    {
        var response = await http.PostAsJsonAsync("/accrue", new { accountType, balance });
        var result = await response.Content.ReadFromJsonAsync<AccrueResponse>()
            ?? throw new InvalidOperationException("Engine returned an empty response");
        return result.Interest;
    }
}
