using System;
using System.Buffers;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace InstallSdkGlobalTool
{
    public class SdkAcquirer
    {
        const string ReleaseIndex = "https://raw.githubusercontent.com/dotnet/core/master/release-notes/releases-index.json";
        
        readonly HttpClient _httpClient;
        readonly ITextWriter _textWriter;
        readonly IInstallerLauncher _installerLauncher;
        readonly IPlatformIdentifier _platformIdentifier;

        public SdkAcquirer(HttpClient httpClient, ITextWriter textWriter, IInstallerLauncher installerLauncher, IPlatformIdentifier platformIdentifier)
        {
            _httpClient = httpClient;
            _textWriter = textWriter;
            _installerLauncher = installerLauncher;
            _platformIdentifier = platformIdentifier;
        }

        public async Task Acquire(string version)
        {
            var channelVersion = ParseChannelVersion(version);
            var platform = _platformIdentifier.GetPlatform();

            using var releasesResponse = await JsonDocument.ParseAsync(await _httpClient.GetStreamAsync(ReleaseIndex));

            var channel = releasesResponse.RootElement.GetProperty("releases-index").EnumerateArray()
                .First(x => x.GetProperty("channel-version").GetString() == channelVersion);

            var channelJson = channel.GetProperty("releases.json").GetString();

            using var channelResponse = await JsonDocument.ParseAsync(await _httpClient.GetStreamAsync(channelJson));

            var file = channelResponse
                .RootElement.GetProperty("releases").EnumerateArray()
                .SelectMany(x =>
                {
                    IEnumerable<JsonElement> GetSdks()
                    {
                        yield return x.GetProperty("sdk");
                        if (x.TryGetProperty("sdks", out var sdks))
                        {
                            foreach (var y in sdks.EnumerateArray())
                                yield return y;
                        }
                    }

                    return GetSdks();
                })
                .First(x => x.GetProperty("version").GetString() == version)
                .GetProperty("files")
                .EnumerateArray()
                .First(x => x.GetProperty("rid").GetString() == platform);

            var name = file.GetProperty("name").GetString();
            var installerUrl = file.GetProperty("url").GetString();
            var fileHash = file.GetProperty("hash").GetString();
            
            var filePath = Path.Combine(Path.GetTempPath(), name);
            using var installerStream = await _httpClient.GetStreamAsync(installerUrl);
            using var fileStream = new FileStream(filePath, FileMode.Create);
            var progress = new Progress<long>();

            var lastReportedBytesMbs = 0;
            progress.ProgressChanged += (sender, totalBytes) =>
            {
                var currentBytesMbs = (int) Math.Floor(totalBytes / Math.Pow(2, 20));
                if (currentBytesMbs <= lastReportedBytesMbs) return;
                lastReportedBytesMbs = currentBytesMbs;
                _textWriter.SetCursorPosition(0, Console.CursorTop);
                _textWriter.Write($"Downloading: {currentBytesMbs}MB");
            };
            await CopyToWithProgress(installerStream, fileStream, progress);

            CheckHash(filePath, fileHash);

            _installerLauncher.Launch(filePath);
        }

        static string ParseChannelVersion(string version)
        {
            var channelVersion = version.Substring(0, 3);
            if (!char.IsDigit(channelVersion[0]) || channelVersion[1] != '.' || !char.IsDigit(channelVersion[2]))
                throw new ArgumentException(@"Parsing channel version failed, expected a major.minor format. e.g. ""2.1""", nameof(version));
            return channelVersion;
        }
        
        static async Task CopyToWithProgress(Stream source, Stream destination, IProgress<long> progress)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            long totalBytesRead = 0;
            try
            {
                while (true)
                {
                    var bytesRead = await source.ReadAsync(new Memory<byte>(buffer));
                    if (bytesRead == 0) break;
                    await destination.WriteAsync(new ReadOnlyMemory<byte>(buffer, 0, bytesRead));
                    totalBytesRead += bytesRead;
                    progress.Report(totalBytesRead);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        void CheckHash(string filePath, string fileHash)
        {
            using var sha512 = new SHA512Managed();
            var hash = sha512.ComputeHash(File.OpenRead(filePath));
            var hashString = BitConverter.ToString(hash).Replace("-", "");
            if (hashString != fileHash)
                _textWriter.WriteLine("The downloaded file contents did not match expected hash.");
        }
    }
}