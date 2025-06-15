using EpicKit.WebAPI.Store.Models;

namespace EpicContentContentDownloader;

public static class ContentDownloader
{
    internal static EpicGamesSession EpicGamesSession = new();

    private static void SaveAccountSettings(EpicKit.SessionAccount settings)
    {
        AccountSettingsStore.Instance.Settings.AccessToken = settings.AccessToken;
        AccountSettingsStore.Instance.Settings.AccessTokenExpiresAt = settings.AccessTokenExpiresAt;
        AccountSettingsStore.Instance.Settings.RefreshToken = settings.RefreshToken;
        AccountSettingsStore.Instance.Settings.RefreshExpiresAt = settings.RefreshExpiresAt;
        AccountSettingsStore.Instance.Settings.AccountId = settings.AccountId;
        AccountSettingsStore.Instance.Save();
    }

    public static async Task<bool> LoginAsync()
    {
        var settings = await EpicGamesSession.LoginAsync(
            AccountSettingsStore.Instance.Settings.AccessToken,
            AccountSettingsStore.Instance.Settings.AccessTokenExpiresAt,
            AccountSettingsStore.Instance.Settings.RefreshToken,
            AccountSettingsStore.Instance.Settings.RefreshExpiresAt
        );

        if (settings == null)
            return false;

        SaveAccountSettings(settings);
        return true;
    }

    public static async Task<bool> LoginWithAuthorizationCodeAsync(string authorizationCode)
    {
        var settings = await EpicGamesSession.LoginWithAuthorizationCodeAsync(authorizationCode);

        if (settings == null)
            return false;

        SaveAccountSettings(settings);
        return true;
    }

    public static async Task<bool> LoginWithSIDAsync(string sessionId)
    {
        var settings = await EpicGamesSession.LoginWithSIDAsync(sessionId);

        if (settings == null)
            return false;

        SaveAccountSettings(settings);
        return true;
    }

    public static async Task<Dictionary<string, ApplicationAsset>> DownloadAppListAsync(string platform, string label)
    {
        return await EpicGamesSession.DownloadAppListAsync(platform, label);
    }

    public static async Task<StoreApplicationInfos> GetGameInfosAsync(string gameNamespace, string catalogItemId, bool includeDlcs)
    {
        return await EpicGamesSession.GetGameInfosAsync(gameNamespace, catalogItemId, includeDlcs);
    }

    public static async Task<EpicKit.ManifestDownloadInfos> GetManifestDownloadInfosAsync(string gameNamespace, string catalogItemId, string appName, string platform, string label)
    {
        return await EpicGamesSession.GetManifestDownloadInfosAsync(gameNamespace, catalogItemId, appName, platform, label);
    }
}
