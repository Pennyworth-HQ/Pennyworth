using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace Pennyworth.Assistant.Local;

/// <param name="Remaining">Null until the speed is known.</param>
public sealed record DownloadProgress(long Done, long Total, double BytesPerSecond, TimeSpan? Remaining, string CurrentFile)
{
    public double Fraction => Total == 0 ? 0 : (double)Done / Total;
}

/// <summary>
/// Downloads a catalogued model file by file into <c>.partial</c> files, resumes with HTTP
/// ranges, checks every file's SHA-256 against the catalogue and only then gives it its name.
/// A file that doesn't match is deleted. Nothing is loaded from a folder that isn't verified.
/// </summary>
public sealed class ModelDownloader(HttpClient http, Uri? baseUrl = null)
{
    public static readonly Uri HuggingFace = new("https://huggingface.co/");

    private const int BufferSize = 1 << 20;
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(250);

    private readonly Uri _baseUrl = baseUrl ?? HuggingFace;

    public async Task DownloadAsync(LocalModel model, string folder, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        LocalModelStore.Unverify(folder);
        long total = model.Size;
        long done = 0;
        var watch = Stopwatch.StartNew();
        long sinceStart = 0;
        TimeSpan lastReport = TimeSpan.Zero;

        void Report(string file, bool force = false)
        {
            if (!force && watch.Elapsed - lastReport < ReportEvery)
            {
                return;
            }

            lastReport = watch.Elapsed;
            double speed = watch.Elapsed.TotalSeconds > 0.5 ? sinceStart / watch.Elapsed.TotalSeconds : 0;
            TimeSpan? remaining = speed > 0 ? TimeSpan.FromSeconds((total - done) / speed) : null;
            progress?.Report(new DownloadProgress(done, total, speed, remaining, file));
        }

        foreach (ModelFile file in model.Files)
        {
            string target = Path.Combine(folder, file.Path);
            string partial = target + ".partial";
            if (File.Exists(target) && new FileInfo(target).Length == file.Size && await HashOf(target, cancellationToken) == file.Sha256)
            {
                done += file.Size;
                Report(file.Path, force: true);
                continue;
            }

            long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (have > file.Size)
            {
                File.Delete(partial);
                have = 0;
            }

            if (have < file.Size)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, model.UrlOf(file, _baseUrl));
                if (have > 0)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                }

                using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    have = 0; // the server ignored the range: start this file again
                }

                await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(partial, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                done += have;
                byte[] buffer = new byte[BufferSize];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    done += read;
                    sinceStart += read;
                    Report(file.Path);
                }
            }
            else
            {
                done += have;
            }

            if (new FileInfo(partial).Length != file.Size || await HashOf(partial, cancellationToken) != file.Sha256)
            {
                File.Delete(partial);
                throw new InvalidDataException($"{file.Path} did not match the expected checksum and was deleted.");
            }

            File.Move(partial, target, overwrite: true);
            Report(file.Path, force: true);
        }

        LocalModelStore.MarkVerified(folder, model);
        progress?.Report(new DownloadProgress(total, total, 0, TimeSpan.Zero, ""));
    }

    private static async Task<string> HashOf(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
