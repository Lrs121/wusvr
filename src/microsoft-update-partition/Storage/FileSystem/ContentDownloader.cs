﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;

namespace Microsoft.PackageGraph.Storage.Local
{
    class ContentDownloader
    {
        public event EventHandler<ContentOperationProgress> OnDownloadProgress;

        public static bool DownloadToStream(
            string source,
            Stream destination,
            CancellationToken cancellationToken)
        {
            using (var client = new HttpClient())
            {
                // Build the range request for the download
                using var updateRequest = new HttpRequestMessage { RequestUri = new Uri(source), Method = HttpMethod.Get };
                // Stream the file
                using HttpResponseMessage response = client
                    .SendAsync(updateRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                if (response.IsSuccessStatusCode)
                {
                    using Stream streamToReadFrom = response.Content.ReadAsStreamAsync(cancellationToken).GetAwaiter().GetResult();
                    // Read in chunks while not at the end and cancellation was not requested
                    byte[] readBuffer = new byte[2097152 * 5];
                    var readBytesCount = streamToReadFrom.Read(readBuffer, 0, readBuffer.Length);
                    while (!cancellationToken.IsCancellationRequested && readBytesCount > 0)
                    {
                        destination.Write(readBuffer, 0, readBytesCount);
                        readBytesCount = streamToReadFrom.Read(readBuffer, 0, readBuffer.Length);
                    }

                    return readBytesCount == 0;
                }
            }

            return false;
        }

        /// <summary>
        /// Downloads a single file belonging to an update package. Supports resuming a partial download
        /// </summary>
        /// <param name="destinationFilePath">Download destination file.</param>
        /// <param name="updateFile">The update file to download.</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public void DownloadToFile(
            string destinationFilePath,
            IContentFile updateFile,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(destinationFilePath))
            {
                // Destination file does not exist; create it and then download it
                using var fileStream = File.Create(destinationFilePath);
                DownloadToStream(fileStream, updateFile, 0, cancellationToken);
            }
            else
            {
                // Destination file exists; if only partially downloaded, seek to the end and resume download
                // from where we left off
                using var fileStream = File.Open(destinationFilePath, FileMode.Open, FileAccess.Write);
                if (fileStream.Length != (long)updateFile.Size)
                {
                    fileStream.Seek(0, SeekOrigin.End);
                    DownloadToStream(fileStream, updateFile, fileStream.Length, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Downloads the specified URL to the destination file stream
        /// </summary>
        /// <param name="destination">The file stream to write content to</param>
        /// <param name="updateFile">The update to download</param>
        /// <param name="startOffset">Offset to resume download at</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public void DownloadToStream(
            Stream destination,
            IContentFile updateFile,
            long startOffset,
            CancellationToken cancellationToken)
        {
            var progress = new ContentOperationProgress()
            {
                File = updateFile,
                Current = startOffset,
                Maximum = (long)updateFile.Size,
                CurrentOperation = PackagesOperationType.DownloadFileProgress
            };

            // Validate starting offset
            if (startOffset >= (long)updateFile.Size)
            {
                throw new Exception($"Start offset {startOffset} cannot be greater than expected file size {updateFile.Size}");
            }

            var url = updateFile.Source;
            var uri = new Uri(url);
            if (uri.Scheme == "file")
            {
                using var source = File.OpenRead(uri.LocalPath);
                destination.Seek(0, SeekOrigin.Begin);
                destination.SetLength(0);
                source.CopyTo(destination);
            }
            else
            {
                using var client = new HttpClient();
                var fileSizeOnServer = GetFileSizeOnServer(client, url, cancellationToken);

                // Make sure our size matches the server's size
                if (fileSizeOnServer != (long)updateFile.Size)
                {
                    throw new Exception($"File size mismatch. Expected {updateFile.Size}, server advertised {fileSizeOnServer}");
                }

                // Build the range request for the download
                using var updateRequest = new HttpRequestMessage { RequestUri = uri, Method = HttpMethod.Get };
                updateRequest.Headers.Range = new RangeHeaderValue((long)startOffset, (long)fileSizeOnServer - 1);

                // Stream the file to disk
                using HttpResponseMessage response = client
                    .SendAsync(updateRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                if (response.IsSuccessStatusCode)
                {
                    using Stream streamToReadFrom = response
                        .Content
                        .ReadAsStreamAsync(cancellationToken)
                        .GetAwaiter()
                        .GetResult();

                    // Read in chunks while not at the end and cancellation was not requested
                    byte[] readBuffer = new byte[2097152 * 5];
                    var readBytesCount = streamToReadFrom.Read(readBuffer, 0, readBuffer.Length);
                    while (!cancellationToken.IsCancellationRequested && readBytesCount > 0)
                    {
                        destination.Write(readBuffer, 0, readBytesCount);

                        progress.Current += readBytesCount;
                        OnDownloadProgress?.Invoke(this, progress);

                        readBytesCount = streamToReadFrom.Read(readBuffer, 0, readBuffer.Length);
                    }
                }
                else
                {
                    throw new Exception($"Failed to get content of update from {url}: {response.ReasonPhrase}");
                }
            }
        }

        public static long GetFileSizeOnServer(HttpClient client, string url, CancellationToken cancellationToken)
        {
            // First get the HEAD to check the server's size for the file
            long fileSizeOnServer;
            using (var request = new HttpRequestMessage { RequestUri = new Uri(url), Method = HttpMethod.Head })
            {
                using var headResponse = client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                if (!headResponse.IsSuccessStatusCode)
                {
                    throw new Exception($"Failed to get HEAD of update from {url}: {headResponse.ReasonPhrase}");
                }

                fileSizeOnServer = headResponse.Content.Headers.ContentLength.Value;
            }

            return fileSizeOnServer;
        }
    }
}
