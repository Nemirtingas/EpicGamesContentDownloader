using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.IO.Compression;
using System.IO.IsolatedStorage;

namespace EpicContentContentDownloader;

public class AccountSettingsStore
{
    public class AccountSettingsDataCommon
    {
        [JsonProperty("version")]
        public uint Version { get; set; }
    }

    public class AccountSettingsDataV1
    {
        [JsonProperty("version")]
        public uint Version { get; set; } = CurrentVersion;


        [JsonProperty("accessToken")]
        public string AccessToken { get; set; }

        [JsonProperty("accessTokenExpiresAt")]
        public DateTimeOffset AccessTokenExpiresAt { get; set; }

        [JsonProperty("refreshToken")]
        public string RefreshToken { get; set; }

        [JsonProperty("refreshExpiresAt")]
        public DateTimeOffset RefreshExpiresAt { get; set; }

        [JsonProperty("accountId")]
        public string AccountId { get; set; }
    }

    private const uint CurrentVersion = 1;

    public AccountSettingsDataV1 Settings { get; private set; } = new();

    string FileName;

    readonly IsolatedStorageFile IsolatedStorage = IsolatedStorageFile.GetUserStoreForAssembly();

    private static AccountSettingsStore _instance;
    public static AccountSettingsStore Instance { get => _instance ??= new AccountSettingsStore(); }

    private AccountSettingsStore()
    {
    }

    public void LoadFromFile(string filename)
    {
        if (IsolatedStorage.FileExists(filename))
        {
            try
            {
                uint version = CurrentVersion;
                var ms = new MemoryStream();
                using (var fs = IsolatedStorage.OpenFile(filename, FileMode.Open, FileAccess.Read))
                using (var ds = new DeflateStream(fs, CompressionMode.Decompress))
                {
                    ds.CopyTo(ms);
                    ms.Position = 0;
                    version = ParseJson<AccountSettingsDataCommon>(ms).Version;
                }

                ms.Position = 0;
                switch (version)
                {
                    case 1: HandleVersion1(ms); break;
                }
            }
            catch (IOException ex)
            {
                Utils.Logger.LogError($"Failed to load account settings: {ex.Message}");
            }
        }

        FileName = filename;
    }

    public void Save()
    {
        try
        {
            using (var fs = IsolatedStorage.OpenFile(FileName, FileMode.Create, FileAccess.Write))
            using (var ds = new DeflateStream(fs, CompressionMode.Compress))
            using (var sw = new StreamWriter(ds) { NewLine = "\n" })
            using (var writer = new JsonTextWriter(sw))
            {
                JsonSerializer.Create(new JsonSerializerSettings { Formatting = Formatting.Indented }).Serialize(writer, Settings);
            }
        }
        catch (IOException ex)
        {
            Utils.Logger.LogError("Failed to save account settings: {0}", ex);
        }
    }

    private T ParseJson<T>(Stream s)
    {
        using (var sr = new StreamReader(s, leaveOpen: true))
        using (var reader = new JsonTextReader(sr))
        {
            return new JsonSerializer().Deserialize<T>(reader);
        }
    }

    private void HandleVersion1(MemoryStream ms)
    {
        Settings = ParseJson<AccountSettingsDataV1>(ms);

        if (Settings == null)
            Settings = new AccountSettingsDataV1();
    }
}
