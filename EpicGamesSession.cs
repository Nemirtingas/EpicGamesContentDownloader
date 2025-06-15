using EpicKit.WebAPI.Store.Models;
using Microsoft.Extensions.Logging;

namespace EpicGamesContentDownloader;

internal class EpicGamesSession
{
    internal EpicKit.WebApi EGSApi = new();

    internal async Task<EpicKit.SessionAccount> LoginAnonymousAsync()
    {
        return await EGSApi.LoginAnonymous();
    }

    internal async Task<EpicKit.SessionAccount> LoginWithAuthorizationCodeAsync(string authorizationCode)
    {
        return await EGSApi.LoginAuthCode(authorizationCode);
    }

    internal async Task<EpicKit.SessionAccount> LoginWithSIDAsync(string sessionId)
    {
        return await EGSApi.LoginSID(sessionId);
    }

    internal async Task<EpicKit.SessionAccount> LoginAsync(string accessToken, DateTimeOffset accessTokenExpiresAt, string refreshToken, DateTimeOffset refreshTokenExpiresAt)
    {
        return await EGSApi.LoginAsync(accessToken, accessTokenExpiresAt, refreshToken, refreshTokenExpiresAt);
    }

    internal async Task<Dictionary<string, ApplicationAsset>> DownloadAppListAsync(string platform, string label)
    {
        Utils.Logger.LogInformation("Downloading assets...");
        var appList = new Dictionary<string, ApplicationAsset>();

        foreach (var appAsset in await EGSApi.GetApplicationsAssets(platform, label))
        {
            if (appAsset.AppName == appAsset.AssetId)
            {
                appList[appAsset.AssetId] = appAsset;
            }
            else
            {
                Utils.Logger.LogInformation($"Asset {appAsset.AssetId} with != asset name {appAsset.AppName}");
            }
        }

        return appList;
    }

    internal async Task<StoreApplicationInfos> GetGameInfosAsync(string gameNamespace, string catalogItemId, bool includeDlcs)
    {
        return await EGSApi.GetGameInfos(gameNamespace, catalogItemId, includeDlcs);
    }

    internal async Task<EpicKit.ManifestDownloadInfos> GetManifestDownloadInfosAsync(string gameNamespace, string catalogItemId, string appName, string platform, string label)
    {
        return await EGSApi.GetManifestDownloadInfos(gameNamespace, catalogItemId, appName, platform, label);
    }
}
