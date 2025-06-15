using EpicKit.Manifest;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace EpicGamesContentDownloader;

public class DownloadPlan
{
    public class DownloadPlanFileCounter
    {
        public string FileName;
        public ulong DownloadedSize;
        public ulong Size;
    }

    public class DownloadPlanGlobalCounter
    {
        public ulong FileDownloaded;
        public ulong FileCount;

        public ulong DownloadedSize;
        public ulong Size;
    }

    private class PlanWebClient
    {
        public HttpClient HttpClient { get; set; } = new HttpClient();
        public CancellationToken CancellationToken { get; set; }
        public bool InUse { get; set; } = false;
    }

    private class WebHost
    {
        public string Url { get; set; }
        public int FailCount { get; set; }
    }

    private class FileWebChunk
    {
        public AsyncMutex Mutex = new();
        public DataChunk DataChunk { get; set; } = null;
        public string ChunkFilePath { get; set; }
        public string WebPath { get; set; } = string.Empty;
        public bool Started { get; set; } = false;
        public int RefCount { get; set; } = 0;
    }

    private class FileDownloadChunk : IDisposable
    {
        public EpicKit.Manifest.Guid Guid = new EpicKit.Manifest.Guid();
        public long FileOffset = 0;
        public ulong ChunkOffset = 0;
        public ulong DataSize = 0;

        public FileWebChunk WebChunk = null;

        public void Dispose()
        {
            WebChunk = null;
        }
    };

    private class FileDownloadPlan
    {
        public string Filename = string.Empty;
        public ulong FileSize = 0;
        public byte[] Sha1 = new byte[20];

        public List<FileDownloadChunk> FileChunks = new();
    }

    private int ParallelCount { get; set; }
    private string OutputDirectory { get; set; }
    private List<PlanWebClient> _WebClients = new();
    private List<WebHost> _WebHosts { get; set; } = new();

    private List<FileDownloadPlan> FilesDownloadPlan = new();

    private DownloadPlanGlobalCounter DownloadPlanCounter = new();

    private async Task<FileDownloadChunk> DownloadChunk(List<PlanWebClient> WebClients, FileDownloadChunk downloadChunk)
    {
        if (downloadChunk == null)
            return downloadChunk;

        DataChunk epic_chunk = new DataChunk();
        var webChunk = downloadChunk.WebChunk;

        Utils.Logger.LogTrace($"Started chunk: {webChunk.ChunkFilePath}");

        using (var lk = await webChunk.Mutex.LockAsync())
        {
            if (webChunk.Started)
            {
                if (webChunk.DataChunk != null)
                {
                    Utils.Logger.LogTrace($"Already processed chunk: {webChunk.ChunkFilePath}");
                    return downloadChunk;
                }

                Utils.Logger.LogTrace($"Already processing chunk: {webChunk.ChunkFilePath}");
                Monitor.Wait(webChunk);
                if (webChunk.DataChunk != null)
                {
                    Utils.Logger.LogTrace($"Already processed chunk: {webChunk.ChunkFilePath}");
                    return downloadChunk;
                }
            }

            webChunk.Started = true;
            webChunk.DataChunk = null;

            try
            {
                using (var fileStream = new FileStream(webChunk.ChunkFilePath, FileMode.Open))
                {
                    epic_chunk.Read(fileStream);
                }

                if (epic_chunk.Version >= 2)
                {
                    byte hashChecked = 0;
                    if ((epic_chunk.HashType & 1) == 1)
                    {
                        if (new Hash().Get(epic_chunk.Data) == epic_chunk.Hash)
                            hashChecked |= 1;
                        else
                            Utils.Logger.LogTrace($"Epic hash didn't match, redownloading chunk: {webChunk.ChunkFilePath}");
                    }
                    if ((epic_chunk.HashType & 2) == 2)
                    {
                        if (SHA1.Create().ComputeHash(epic_chunk.Data).SequenceEqual(epic_chunk.Sha1Hash))
                            hashChecked |= 2;
                        else
                            Utils.Logger.LogTrace($"SHA1 hash didn't match, redownloading chunk: {webChunk.ChunkFilePath}");
                    }

                    if (hashChecked == epic_chunk.HashType)
                    {
                        Utils.Logger.LogTrace($"Validated chunk: {webChunk.ChunkFilePath}");
                        webChunk.DataChunk = epic_chunk;
                        return downloadChunk;
                    }
                }
                else
                {
                    if (new Hash().Get(epic_chunk.Data) == epic_chunk.Hash)
                    {
                        Utils.Logger.LogTrace($"Validated chunk: {webChunk.ChunkFilePath}");
                        webChunk.DataChunk = epic_chunk;
                        return downloadChunk;
                    }

                    Utils.Logger.LogTrace($"Epic hash didn't match, redownloading chunk: {webChunk.ChunkFilePath}");
                }
            }
            catch
            {
            }
        }

        WebHost webHost = null;

        for (int i = 0; i < 10; ++i)
        {
            var webClient = default(PlanWebClient);
            try
            {
                Utils.Logger.LogTrace($"Waiting for webclient chunk: {webChunk.ChunkFilePath}");
                while (webClient == null)
                {
                    foreach (var webCli in WebClients)
                    {
                        using (var lk = await webChunk.Mutex.LockAsync())
                        {
                            if (!webCli.InUse)
                            {
                                webCli.InUse = true;
                                webClient = webCli;
                                break;
                            }
                        }
                    }

                    if (webClient == null)
                        await Task.Delay(TimeSpan.FromMilliseconds(150));
                }

                // Find the host with fewer fails
                foreach (var item in _WebHosts)
                {
                    if (webHost == null || item.FailCount < webHost.FailCount)
                    {
                        webHost = item;
                    }
                }

                Utils.Logger.LogTrace($"Downloading chunk: {webChunk.ChunkFilePath}");

                var request = new HttpRequestMessage(HttpMethod.Get, $"{webHost.Url}/{webChunk.WebPath}");
                // Some content host are very buggy and doesn't respond to the web request, so put a timeout here.
                var resp = await webClient.HttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, webClient.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15));

                var chunkStream = await resp.Content.ReadAsStreamAsync();

                try
                {
                    if (!Directory.Exists(Path.GetDirectoryName(webChunk.ChunkFilePath)))
                        Directory.CreateDirectory(Path.GetDirectoryName(webChunk.ChunkFilePath));

                    using (var fileStream = new FileStream(webChunk.ChunkFilePath, FileMode.Create))
                    {
                        await chunkStream.CopyToAsync(fileStream);
                    }
                }
                catch
                { }

                chunkStream.Position = 0;

                epic_chunk.Read(chunkStream);
                webChunk.DataChunk = epic_chunk;

                Utils.Logger.LogTrace($"Downloaded chunk: {webChunk.ChunkFilePath}");
                break;
            }
            catch (TimeoutException)
            {
                ++webHost.FailCount;
                using (var lk = await webChunk.Mutex.LockAsync())
                {
                    webChunk.Started = false;
                }
            }
            catch (Exception)
            {
                ++webHost.FailCount;
            }
            finally
            {
                using (var lk = await webChunk.Mutex.LockAsync())
                {
                    webClient.InUse = false;
                }
            }
        }

        using (var lk = await webChunk.Mutex.LockAsync())
        {
            if (webChunk.DataChunk == null)
                webChunk.Started = false;
        }

        Utils.Logger.LogTrace($"Finished chunk: {webChunk.ChunkFilePath}");
        return downloadChunk;
    }

    public static async Task<DownloadPlan> BuildDownloadPlanAsync(DownloadConfiguration downloadConfig, Manifest manifest)
    {
        var plan = new DownloadPlan();
        var commonWebChunks = new Dictionary<EpicKit.Manifest.Guid, FileWebChunk>();

        plan.ParallelCount = downloadConfig.ParallelClientCount;
        plan.OutputDirectory = downloadConfig.OutputDirectory;

        // Older manifests has a BaseUrl CustomFields, so try to read it.
        var manifestBaseUrls = new List<string>();

        if (manifest.CustomFieldsList.CustomFields.TryGetValue("BaseUrl", out var v))
            manifestBaseUrls.AddRange(v.Split(","));

        plan._WebHosts.AddRange(manifestBaseUrls.Concat(downloadConfig.BaseUrls).ToHashSet().Select(e => new WebHost
        {
            Url = e,
            FailCount = 0
        }));

        for (int i = 0; i < plan.ParallelCount; ++i)
        {
            var cli = new PlanWebClient();
            cli.HttpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit");
            plan._WebClients.Add(cli);
        }

        int maxFileCount = manifest.FileManifestList.FileManifests.Count;
        plan.DownloadPlanCounter.FileCount = (ulong)maxFileCount;

        foreach (var fileManifest in manifest.FileManifestList.FileManifests)
        {
            var fileDownloadPlan = new FileDownloadPlan();
            fileDownloadPlan.Filename = fileManifest.Filename;
            fileDownloadPlan.FileSize = fileManifest.FileSize;
            fileManifest.Sha1Hash.CopyTo(fileDownloadPlan.Sha1, 0);
            fileDownloadPlan.FileChunks = new List<FileDownloadChunk>(fileManifest.FileChunks.Count);

            plan.DownloadPlanCounter.Size += fileManifest.FileSize;

            await Task.Run(() =>
            {
                long fileOffset = 0;
                foreach (var file_chunk in fileManifest.FileChunks)
                {
                    foreach (var data_chunk in manifest.ChunkDataList.ChunkData)
                    {
                        if (file_chunk.Guid.Equals(data_chunk.Guid))
                        {
                            FileDownloadChunk fdc = new FileDownloadChunk
                            {
                                FileOffset = fileOffset,
                                ChunkOffset = file_chunk.Offset,
                                DataSize = file_chunk.Size,
                                Guid = data_chunk.Guid,
                            };

                            fileOffset += file_chunk.Size;

                            if (commonWebChunks.ContainsKey(data_chunk.Guid))
                            {
                                fdc.WebChunk = commonWebChunks[data_chunk.Guid];
                                ++fdc.WebChunk.RefCount;
                            }
                            else
                            {
                                fdc.WebChunk = new FileWebChunk
                                {
                                    WebPath = data_chunk.Path(),
                                    RefCount = 1,
                                };

                                fdc.WebChunk.ChunkFilePath = Path.Combine(plan.OutputDirectory, ".egstore", "pbs", fdc.WebChunk.WebPath);

                                commonWebChunks[data_chunk.Guid] = fdc.WebChunk;
                            }

                            fileDownloadPlan.FileChunks.Add(fdc);
                            break;
                        }
                    }
                }
            });

            plan.FilesDownloadPlan.Add(fileDownloadPlan);
        }

        return plan;
    }

    private void CleanChunks(List<string> chunksToDelete)
    {
        foreach (var chunkPath in chunksToDelete)
        {
            try
            {
                File.Delete(chunkPath);
                Directory.Delete(Path.GetDirectoryName(chunkPath));
            }
            catch
            { }
        }
    }

    private async Task<bool> CheckFileIntegrityAsync(string filePath, List<FileDownloadChunk> fileChunks, byte[] shaHash)
    {
        try
        {
            var sha1 = SHA1.Create();
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                if (shaHash.SequenceEqual(await sha1.ComputeHashAsync(fs)))
                {
                    var chunksToDelete = new List<string>();

                    foreach (var fileChunk in fileChunks)
                    {
                        --fileChunk.WebChunk.RefCount;
                        if (fileChunk.WebChunk.RefCount == 0)
                            chunksToDelete.Add(fileChunk.WebChunk.ChunkFilePath);
                    }

                    CleanChunks(chunksToDelete);

                    return true;
                }
            }
        }
        catch
        { }

        return false;
    }

    public Task RunDownloadPlanAsync(CancellationTokenSource cts)
        => RunDownloadPlanAsync(cts, null);

    public async Task RunDownloadPlanAsync(CancellationTokenSource cts, Action<DownloadPlanGlobalCounter, DownloadPlanFileCounter> downloadReportCallback)
    {
        foreach (var webClient in _WebClients)
            webClient.CancellationToken = cts.Token;

        foreach (var filePlan in FilesDownloadPlan)
        {
            var chunksToDelete = new List<string>();
            var outputFileName = Path.Combine(OutputDirectory, filePlan.Filename);
            Directory.CreateDirectory(Path.GetDirectoryName(outputFileName));
            var chunkCount = filePlan.FileChunks.Count;
            var downloadedChunkCount = 0;

            var fileCounter = new DownloadPlanFileCounter
            {
                FileName = filePlan.Filename,
                Size = filePlan.FileSize
            };

            cts.Token.ThrowIfCancellationRequested();

            if (await CheckFileIntegrityAsync(outputFileName, filePlan.FileChunks, filePlan.Sha1))
            {
                ++DownloadPlanCounter.FileDownloaded;
                DownloadPlanCounter.DownloadedSize += filePlan.FileSize;

                fileCounter.DownloadedSize = fileCounter.Size;

                Utils.Logger.LogInformation(string.Format("Downloading {0:0.0}% - {1} 100%",
                    100 * ((double)DownloadPlanCounter.DownloadedSize / DownloadPlanCounter.Size),
                    filePlan.Filename));

                if (downloadReportCallback != null)
                    downloadReportCallback(DownloadPlanCounter, fileCounter);

                continue;
            }

            Utils.Logger.LogInformation(string.Format("Downloading {0:0.0}% - {1} 0% | {2}/{3} chunks",
                            100 * ((double)DownloadPlanCounter.DownloadedSize / DownloadPlanCounter.Size),
                            filePlan.Filename,
                            0, chunkCount));

            using (FileStream fs = new FileStream(outputFileName, FileMode.Create, FileAccess.Write))
            {
                var webWorkers = new List<Task<FileDownloadChunk>>();

                for (var i = filePlan.FileChunks.Count - 1; i >= 0 && webWorkers.Count < ParallelCount; --i)
                {
                    webWorkers.Add(DownloadChunk(_WebClients, filePlan.FileChunks[i]));
                    filePlan.FileChunks.RemoveAt(i);
                }

                while (webWorkers.Count > 0)
                {
                    var completedTask = await Task.WhenAny(webWorkers).WaitAsync(cts.Token);

                    cts.Token.ThrowIfCancellationRequested();

                    webWorkers.Remove(completedTask);
                    var downloadResult = await completedTask;
                    var webChunk = downloadResult.WebChunk;

                    if (webChunk.DataChunk == null)
                    {
                        webWorkers.Add(DownloadChunk(_WebClients, downloadResult));
                        continue;
                    }

                    if (filePlan.FileChunks.Count > 0)
                    {
                        webWorkers.Add(DownloadChunk(_WebClients, filePlan.FileChunks[filePlan.FileChunks.Count - 1]));
                        filePlan.FileChunks.RemoveAt(filePlan.FileChunks.Count - 1);
                    }

                    using (downloadResult)
                    {
                        fs.Position = downloadResult.FileOffset;
                        await fs.WriteAsync(webChunk.DataChunk.Data, (int)downloadResult.ChunkOffset, (int)downloadResult.DataSize);

                        fileCounter.DownloadedSize += downloadResult.DataSize;
                        DownloadPlanCounter.DownloadedSize += downloadResult.DataSize;

                        Utils.Logger.LogInformation(string.Format("Downloading {0:0.0}% - {1} {2:0.0}% | {3}/{4} chunks",
                            100 * ((double)DownloadPlanCounter.DownloadedSize / DownloadPlanCounter.Size),
                            filePlan.Filename,
                            100 * ((float)fileCounter.DownloadedSize / fileCounter.Size),
                            ++downloadedChunkCount, chunkCount));

                        if (downloadReportCallback != null)
                            downloadReportCallback(DownloadPlanCounter, fileCounter);
                    }

                    --webChunk.RefCount;
                    if (webChunk.RefCount == 0)
                        chunksToDelete.Add(webChunk.ChunkFilePath);
                }
            }

            CleanChunks(chunksToDelete);
        }
    }
}
