﻿using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.MediaEncoding.Subtitles
{
    public class SubtitleEncoder : ISubtitleEncoder
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly IApplicationPaths _appPaths;
        private readonly IFileSystem _fileSystem;
        private readonly IMediaEncoder _mediaEncoder;

        public SubtitleEncoder(ILibraryManager libraryManager, ILogger logger, IApplicationPaths appPaths, IFileSystem fileSystem, IMediaEncoder mediaEncoder)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _appPaths = appPaths;
            _fileSystem = fileSystem;
            _mediaEncoder = mediaEncoder;
        }

        private string SubtitleCachePath
        {
            get
            {
                return Path.Combine(_appPaths.CachePath, "subtitles");
            }
        }

        public async Task<Stream> ConvertSubtitles(Stream stream,
            string inputFormat,
            string outputFormat,
            CancellationToken cancellationToken)
        {
            var ms = new MemoryStream();

            try
            {
                if (string.Equals(inputFormat, outputFormat, StringComparison.OrdinalIgnoreCase))
                {
                    await stream.CopyToAsync(ms, 81920, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var trackInfo = await GetTrackInfo(stream, inputFormat, cancellationToken).ConfigureAwait(false);

                    var writer = GetWriter(outputFormat);

                    writer.Write(trackInfo, ms, cancellationToken);
                }
                ms.Position = 0;
            }
            catch
            {
                ms.Dispose();
                throw;
            }

            return ms;
        }

        public async Task<Stream> GetSubtitles(string itemId,
            string mediaSourceId,
            int subtitleStreamIndex,
            string outputFormat,
            CancellationToken cancellationToken)
        {
            var subtitle = await GetSubtitleStream(itemId, mediaSourceId, subtitleStreamIndex, cancellationToken)
                        .ConfigureAwait(false);

            using (var stream = subtitle.Item1)
            {
                var inputFormat = subtitle.Item2;

                return await ConvertSubtitles(stream, inputFormat, outputFormat, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<Tuple<Stream, string>> GetSubtitleStream(string itemId,
            string mediaSourceId,
            int subtitleStreamIndex,
            CancellationToken cancellationToken)
        {
            var item = (Video)_libraryManager.GetItemById(new Guid(itemId));

            var mediaSource = item.GetMediaSources(false)
                .First(i => string.Equals(i.Id, mediaSourceId));

            var subtitleStream = mediaSource.MediaStreams
                .First(i => i.Type == MediaStreamType.Subtitle && i.Index == subtitleStreamIndex);

            var inputType = mediaSource.LocationType == LocationType.Remote ? InputType.Url : InputType.File;
            var inputFiles = new[] { mediaSource.Path };

            if (mediaSource.VideoType.HasValue)
            {
                if (mediaSource.VideoType.Value == VideoType.BluRay)
                {
                    inputType = InputType.Bluray;
                    var mediaSourceItem = (Video)_libraryManager.GetItemById(new Guid(mediaSourceId));
                    inputFiles = mediaSourceItem.GetPlayableStreamFiles().ToArray();
                }
                else if (mediaSource.VideoType.Value == VideoType.Dvd)
                {
                    inputType = InputType.Dvd;
                    var mediaSourceItem = (Video)_libraryManager.GetItemById(new Guid(mediaSourceId));
                    inputFiles = mediaSourceItem.GetPlayableStreamFiles().ToArray();
                }
            }

            var fileInfo = await GetReadableFile(mediaSource.Path, inputFiles, inputType, subtitleStream, cancellationToken).ConfigureAwait(false);

            var stream = File.OpenRead(fileInfo.Item1);

            return new Tuple<Stream, string>(stream, fileInfo.Item2);
        }

        private async Task<Tuple<string, string>> GetReadableFile(string mediaPath,
            string[] inputFiles,
            InputType type,
            MediaStream subtitleStream,
            CancellationToken cancellationToken)
        {
            if (!subtitleStream.IsExternal)
            {
                // Extract    
                var outputPath = GetSubtitleCachePath(mediaPath, subtitleStream.Index, ".ass");

                await ExtractTextSubtitle(inputFiles, type, subtitleStream.Index, false, outputPath, cancellationToken)
                        .ConfigureAwait(false);

                return new Tuple<string, string>(outputPath, "ass");
            }

            var currentFormat = (Path.GetExtension(subtitleStream.Path) ?? subtitleStream.Codec)
                .TrimStart('.');

            if (GetReader(currentFormat, false) == null)
            {
                // Convert    
                var outputPath = GetSubtitleCachePath(mediaPath, subtitleStream.Index, ".ass");

                await ConvertTextSubtitleToAss(subtitleStream.Path, outputPath, subtitleStream.Language, cancellationToken)
                        .ConfigureAwait(false);

                return new Tuple<string, string>(outputPath, "ass");
            }

            return new Tuple<string, string>(subtitleStream.Path, currentFormat);
        }

        private async Task<SubtitleTrackInfo> GetTrackInfo(Stream stream,
            string inputFormat,
            CancellationToken cancellationToken)
        {
            var reader = GetReader(inputFormat, true);

            return reader.Parse(stream, cancellationToken);
        }

        private ISubtitleParser GetReader(string format, bool throwIfMissing)
        {
            if (string.IsNullOrEmpty(format))
            {
                throw new ArgumentNullException("format");
            }

            if (string.Equals(format, SubtitleFormat.SRT, StringComparison.OrdinalIgnoreCase))
            {
                return new SrtParser();
            }
            if (string.Equals(format, SubtitleFormat.SSA, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(format, SubtitleFormat.ASS, StringComparison.OrdinalIgnoreCase))
            {
                return new SsaParser();
            }

            if (throwIfMissing)
            {
                throw new ArgumentException("Unsupported format: " + format);
            }

            return null;
        }

        private ISubtitleWriter GetWriter(string format)
        {
            if (string.IsNullOrEmpty(format))
            {
                throw new ArgumentNullException("format");
            }

            if (string.Equals(format, SubtitleFormat.VTT, StringComparison.OrdinalIgnoreCase))
            {
                return new VttWriter();
            }

            throw new ArgumentException("Unsupported format: " + format);
        }

        /// <summary>
        /// The _semaphoreLocks
        /// </summary>
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphoreLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>();

        /// <summary>
        /// Gets the lock.
        /// </summary>
        /// <param name="filename">The filename.</param>
        /// <returns>System.Object.</returns>
        private SemaphoreSlim GetLock(string filename)
        {
            return _semaphoreLocks.GetOrAdd(filename, key => new SemaphoreSlim(1, 1));
        }

        /// <summary>
        /// Converts the text subtitle to ass.
        /// </summary>
        /// <param name="inputPath">The input path.</param>
        /// <param name="outputPath">The output path.</param>
        /// <param name="language">The language.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task ConvertTextSubtitleToAss(string inputPath, string outputPath, string language,
            CancellationToken cancellationToken)
        {
            var semaphore = GetLock(outputPath);

            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (!File.Exists(outputPath))
                {
                    await ConvertTextSubtitleToAssInternal(inputPath, outputPath, language).ConfigureAwait(false);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Converts the text subtitle to ass.
        /// </summary>
        /// <param name="inputPath">The input path.</param>
        /// <param name="outputPath">The output path.</param>
        /// <param name="language">The language.</param>
        /// <returns>Task.</returns>
        /// <exception cref="System.ArgumentNullException">inputPath
        /// or
        /// outputPath</exception>
        /// <exception cref="System.ApplicationException"></exception>
        private async Task ConvertTextSubtitleToAssInternal(string inputPath, string outputPath, string language)
        {
            if (string.IsNullOrEmpty(inputPath))
            {
                throw new ArgumentNullException("inputPath");
            }

            if (string.IsNullOrEmpty(outputPath))
            {
                throw new ArgumentNullException("outputPath");
            }


            var encodingParam = string.IsNullOrEmpty(language)
                ? string.Empty
                : _mediaEncoder.GetSubtitleLanguageEncodingParam(inputPath, language);

            if (!string.IsNullOrEmpty(encodingParam))
            {
                encodingParam = " -sub_charenc " + encodingParam;
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,

                    CreateNoWindow = true,
                    UseShellExecute = false,
                    FileName = _mediaEncoder.EncoderPath,
                    Arguments =
                        string.Format("{0} -i \"{1}\" -c:s ass \"{2}\"", encodingParam, inputPath, outputPath),

                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false
                }
            };

            _logger.Debug("{0} {1}", process.StartInfo.FileName, process.StartInfo.Arguments);

            var logFilePath = Path.Combine(_appPaths.LogDirectoryPath, "ffmpeg-sub-convert-" + Guid.NewGuid() + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath));

            var logFileStream = _fileSystem.GetFileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read,
                true);

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                logFileStream.Dispose();

                _logger.ErrorException("Error starting ffmpeg", ex);

                throw;
            }

            var logTask = process.StandardError.BaseStream.CopyToAsync(logFileStream);

            var ranToCompletion = process.WaitForExit(60000);

            if (!ranToCompletion)
            {
                try
                {
                    _logger.Info("Killing ffmpeg subtitle conversion process");

                    process.Kill();

                    process.WaitForExit(1000);

                    await logTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error killing subtitle conversion process", ex);
                }
                finally
                {
                    logFileStream.Dispose();
                }
            }

            var exitCode = ranToCompletion ? process.ExitCode : -1;

            process.Dispose();

            var failed = false;

            if (exitCode == -1)
            {
                failed = true;

                if (File.Exists(outputPath))
                {
                    try
                    {
                        _logger.Info("Deleting converted subtitle due to failure: ", outputPath);
                        File.Delete(outputPath);
                    }
                    catch (IOException ex)
                    {
                        _logger.ErrorException("Error deleting converted subtitle {0}", ex, outputPath);
                    }
                }
            }
            else if (!File.Exists(outputPath))
            {
                failed = true;
            }

            if (failed)
            {
                var msg = string.Format("ffmpeg subtitle converted failed for {0}", inputPath);

                _logger.Error(msg);

                throw new ApplicationException(msg);
            }
            await SetAssFont(outputPath).ConfigureAwait(false);
        }

        /// <summary>
        /// Extracts the text subtitle.
        /// </summary>
        /// <param name="inputFiles">The input files.</param>
        /// <param name="type">The type.</param>
        /// <param name="subtitleStreamIndex">Index of the subtitle stream.</param>
        /// <param name="copySubtitleStream">if set to true, copy stream instead of converting.</param>
        /// <param name="outputPath">The output path.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        /// <exception cref="System.ArgumentException">Must use inputPath list overload</exception>
        private async Task ExtractTextSubtitle(string[] inputFiles, InputType type, int subtitleStreamIndex,
            bool copySubtitleStream, string outputPath, CancellationToken cancellationToken)
        {
            var semaphore = GetLock(outputPath);

            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (!File.Exists(outputPath))
                {
                    await ExtractTextSubtitleInternal(_mediaEncoder.GetInputArgument(inputFiles, type), subtitleStreamIndex,
                            copySubtitleStream, outputPath, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Extracts the text subtitle.
        /// </summary>
        /// <param name="inputPath">The input path.</param>
        /// <param name="subtitleStreamIndex">Index of the subtitle stream.</param>
        /// <param name="copySubtitleStream">if set to true, copy stream instead of converting.</param>
        /// <param name="outputPath">The output path.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        /// <exception cref="System.ArgumentNullException">inputPath
        /// or
        /// outputPath
        /// or
        /// cancellationToken</exception>
        /// <exception cref="System.ApplicationException"></exception>
        private async Task ExtractTextSubtitleInternal(string inputPath, int subtitleStreamIndex,
            bool copySubtitleStream, string outputPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(inputPath))
            {
                throw new ArgumentNullException("inputPath");
            }

            if (string.IsNullOrEmpty(outputPath))
            {
                throw new ArgumentNullException("outputPath");
            }

            string processArgs = string.Format("-i {0} -map 0:{1} -an -vn -c:s ass \"{2}\"", inputPath,
                subtitleStreamIndex, outputPath);

            if (copySubtitleStream)
            {
                processArgs = string.Format("-i {0} -map 0:{1} -an -vn -c:s copy \"{2}\"", inputPath,
                    subtitleStreamIndex, outputPath);
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,

                    RedirectStandardOutput = false,
                    RedirectStandardError = true,

                    FileName = _mediaEncoder.EncoderPath,
                    Arguments = processArgs,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false
                }
            };

            _logger.Debug("{0} {1}", process.StartInfo.FileName, process.StartInfo.Arguments);

            var logFilePath = Path.Combine(_appPaths.LogDirectoryPath, "ffmpeg-sub-extract-" + Guid.NewGuid() + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath));

            var logFileStream = _fileSystem.GetFileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read,
                true);

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                logFileStream.Dispose();

                _logger.ErrorException("Error starting ffmpeg", ex);

                throw;
            }

            process.StandardError.BaseStream.CopyToAsync(logFileStream);

            var ranToCompletion = process.WaitForExit(60000);

            if (!ranToCompletion)
            {
                try
                {
                    _logger.Info("Killing ffmpeg subtitle extraction process");

                    process.Kill();

                    process.WaitForExit(1000);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error killing subtitle extraction process", ex);
                }
                finally
                {
                    logFileStream.Dispose();
                }
            }

            var exitCode = ranToCompletion ? process.ExitCode : -1;

            process.Dispose();

            var failed = false;

            if (exitCode == -1)
            {
                failed = true;

                if (File.Exists(outputPath))
                {
                    try
                    {
                        _logger.Info("Deleting extracted subtitle due to failure: ", outputPath);
                        File.Delete(outputPath);
                    }
                    catch (IOException ex)
                    {
                        _logger.ErrorException("Error deleting extracted subtitle {0}", ex, outputPath);
                    }
                }
            }
            else if (!File.Exists(outputPath))
            {
                failed = true;
            }

            if (failed)
            {
                var msg = string.Format("ffmpeg subtitle extraction failed for {0} to {1}", inputPath, outputPath);

                _logger.Error(msg);

                throw new ApplicationException(msg);
            }
            else
            {
                var msg = string.Format("ffmpeg subtitle extraction completed for {0} to {1}", inputPath, outputPath);

                _logger.Info(msg);
            }

            await SetAssFont(outputPath).ConfigureAwait(false);
        }

        /// <summary>
        /// Sets the ass font.
        /// </summary>
        /// <param name="file">The file.</param>
        /// <returns>Task.</returns>
        private async Task SetAssFont(string file)
        {
            _logger.Info("Setting ass font within {0}", file);

            string text;
            Encoding encoding;

            using (var reader = new StreamReader(file, true))
            {
                encoding = reader.CurrentEncoding;

                text = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var newText = text.Replace(",Arial,", ",Arial Unicode MS,");

            if (!string.Equals(text, newText))
            {
                using (var writer = new StreamWriter(file, false, encoding))
                {
                    writer.Write(newText);
                }
            }
        }

        private string GetSubtitleCachePath(string mediaPath, int subtitleStreamIndex, string outputSubtitleExtension)
        {
            var ticksParam = string.Empty;

            var date = _fileSystem.GetLastWriteTimeUtc(mediaPath);

            var filename = (mediaPath + "_" + subtitleStreamIndex.ToString(CultureInfo.InvariantCulture) + "_" + date.Ticks.ToString(CultureInfo.InvariantCulture) + ticksParam).GetMD5() + outputSubtitleExtension;

            var prefix = filename.Substring(0, 1);

            return Path.Combine(SubtitleCachePath, prefix, filename);
        }
    }
}
