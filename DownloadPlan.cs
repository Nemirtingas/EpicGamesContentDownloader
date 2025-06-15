using EpicKit.Manifest;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace EpicContentContentDownloader;

public class DownloadPlan
{
    private class PlanWebClient
    {
        public HttpClient HttpClient { get; set; } = new HttpClient();
        public bool InUse { get; set; } = false;
    }

    private class WebHost
    {
        public string Url { get; set; }
        public int FailCount { get; set; }
    }

    private class FileWebChunk
    {
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

        public List<FileDownloadChunk> FileChunks = new List<FileDownloadChunk>();
    }

    private uint ParallelCount { get; set; }
    private string OutputDirectory { get; set; }
    private List<PlanWebClient> _WebClients = new List<PlanWebClient>();
    private List<WebHost> _WebHosts { get; set; } = new List<WebHost>();

    private List<FileDownloadPlan> FilesDownloadPlan = new List<FileDownloadPlan>();
    private ulong TotalDownloadSize = 0;

    private async Task<FileDownloadChunk> DownloadChunk(List<PlanWebClient> WebClients, FileDownloadChunk downloadChunk)
    {
        if (downloadChunk == null)
            return downloadChunk;

        DataChunk epic_chunk = new DataChunk();
        var webChunk = downloadChunk.WebChunk;

        Utils.Logger.LogTrace($"Started chunk: {webChunk.ChunkFilePath}");

        lock (webChunk)
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

        WebHost web_host = null;

        for (int i = 0; i < 10; ++i)
        {
            CancellationTokenSource ct = new CancellationTokenSource();
            PlanWebClient webClient = null;
            try
            {
                Utils.Logger.LogTrace($"Waiting for webclient chunk: {webChunk.ChunkFilePath}");
                while (webClient == null)
                {
                    webClient = WebClients.FirstOrDefault(wc =>
                    {
                        lock (wc)
                        {
                            if (!wc.InUse)
                            {
                                wc.InUse = true;
                                return true;
                            }
                        }

                        return false;
                    });

                    if (webClient == null)
                        await Task.Delay(TimeSpan.FromMilliseconds(150));
                }

                // Find the host with fewer fails
                foreach (var item in _WebHosts)
                {
                    if (web_host == null || item.FailCount < web_host.FailCount)
                    {
                        web_host = item;
                    }
                }

                Utils.Logger.LogTrace($"Downloading chunk: {webChunk.ChunkFilePath}");

                var request = new HttpRequestMessage(HttpMethod.Get, $"{web_host.Url}/{webChunk.WebPath}");
                // Some content host are very buggy and doesn't respond to the web request, so put a timeout here.
                var resp = await webClient.HttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct.Token).WaitAsync(TimeSpan.FromSeconds(15));

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
                ++web_host.FailCount;
                lock (webChunk)
                {
                    webChunk.Started = false;
                    Monitor.PulseAll(webChunk);
                }
                ct.Cancel();
            }
            catch (Exception)
            {
                ++web_host.FailCount;
            }
            finally
            {
                lock (webClient)
                {
                    webClient.InUse = false;
                }
            }
        }

        lock (webChunk)
        {
            if (webChunk.DataChunk == null)
                webChunk.Started = false;

            Monitor.PulseAll(webChunk);
        }

        Utils.Logger.LogTrace($"Finished chunk: {webChunk.ChunkFilePath}");
        return downloadChunk;
    }

    public static async Task<DownloadPlan> BuildDownloadPlanAsync(DownloadConfig downloadConfig, Manifest manifest)
    {
        var plan = new DownloadPlan();
        var commonWebChunks = new Dictionary<EpicKit.Manifest.Guid, FileWebChunk>();

        plan.ParallelCount = downloadConfig.ParallelClientCount;
        plan.OutputDirectory = downloadConfig.OutputDirectory;
        plan._WebHosts.AddRange(manifest.CustomFieldsList.CustomFields["BaseUrl"].Split(",").Select(e => new WebHost
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

        int max_file_count = manifest.FileManifestList.FileManifests.Count;

        foreach (var fileManifest in manifest.FileManifestList.FileManifests)
        {
            var fileDownloadPlan = new FileDownloadPlan();
            fileDownloadPlan.Filename = fileManifest.Filename;
            fileDownloadPlan.FileSize = fileManifest.FileSize;
            fileManifest.Sha1Hash.CopyTo(fileDownloadPlan.Sha1, 0);
            fileDownloadPlan.FileChunks = new List<FileDownloadChunk>(fileManifest.FileChunks.Count);

            plan.TotalDownloadSize += fileManifest.FileSize;

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

    private async Task<bool> CheckFileIntegrity(string filePath, List<FileDownloadChunk> fileChunks, byte[] shaHash)
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

    public async Task RunDownloadPlan()
    {
        ulong totalDownloadedSize = 0;

        foreach (var filePlan in FilesDownloadPlan)
        {
            var chunksToDelete = new List<string>();
            var outputFileName = Path.Combine(OutputDirectory, filePlan.Filename);
            Directory.CreateDirectory(Path.GetDirectoryName(outputFileName));
            ulong downloadedSize = 0;
            var chunkCount = filePlan.FileChunks.Count;
            var downloadedChunkCount = 0;

            if (await CheckFileIntegrity(outputFileName, filePlan.FileChunks, filePlan.Sha1))
            {
                totalDownloadedSize += filePlan.FileSize;

                Utils.Logger.LogInformation(string.Format("Downloading {0:0.0}% - {1} 100%",
                    100 * ((double)totalDownloadedSize / TotalDownloadSize),
                    filePlan.Filename));

                continue;
            }

            Utils.Logger.LogInformation(string.Format("Downloading {0:0.0}% - {1} 0% | {2}/{3} chunks",
                            100 * ((double)totalDownloadedSize / TotalDownloadSize),
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
                    var completedTask = await Task.WhenAny(webWorkers);
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
                        fs.Write(webChunk.DataChunk.Data, (int)downloadResult.ChunkOffset, (int)downloadResult.DataSize);
                        downloadedSize += downloadResult.DataSize;
                        totalDownloadedSize += downloadResult.DataSize;

                        Utils.Logger.LogInformation(string.Format("Downloading {0:0.0}% - {1} {2:0.0}% | {3}/{4} chunks",
                            100 * ((double)totalDownloadedSize / TotalDownloadSize),
                            filePlan.Filename,
                            100 * ((float)downloadedSize / filePlan.FileSize),
                            ++downloadedChunkCount, chunkCount));
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
