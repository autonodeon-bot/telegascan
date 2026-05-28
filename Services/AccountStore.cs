using System.IO;
using System.Text.Json;

namespace TelegaScan.Services;

public sealed class TelegramAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    public string Phone { get; set; } = "";
}

public sealed class AccountStore
{
    private static string AccountsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TelegaScan",
        "accounts.json");

    public List<TelegramAccount> Accounts { get; set; } = new();
    public string ActiveAccountId { get; set; } = "";

    public static AccountStore Load()
    {
        try
        {
            if (File.Exists(AccountsPath))
            {
                var s = JsonSerializer.Deserialize<AccountStore>(File.ReadAllText(AccountsPath));
                if (s != null) return s;
            }
        }
        catch { /* ignore */ }
        return new AccountStore();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(AccountsPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(AccountsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public TelegramAccount GetOrCreateDefault(string phone)
    {
        var existing = Accounts.FirstOrDefault(a => a.Phone == phone);
        if (existing != null) return existing;
        var acc = new TelegramAccount { Phone = phone, DisplayName = phone };
        Accounts.Add(acc);
        if (string.IsNullOrEmpty(ActiveAccountId)) ActiveAccountId = acc.Id;
        Save();
        return acc;
    }

    public TelegramAccount? GetActive() =>
        Accounts.FirstOrDefault(a => a.Id == ActiveAccountId) ?? Accounts.FirstOrDefault();

    public static string SessionPathFor(TelegramAccount account) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TelegaScan",
            $"WTelegram.{account.Id}.session");
}
