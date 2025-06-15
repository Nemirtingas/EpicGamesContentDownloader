namespace EpicGamesContentDownloader;

public class DownloadConfiguration
{
    public string OutputDirectory { get; set; }
    public int ParallelClientCount { get; set; } = 4;
    public List<string> BaseUrls { get; } = new();
}
