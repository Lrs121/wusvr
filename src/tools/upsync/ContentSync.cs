// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using System.Linq;
using System.Threading;
using System;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Storage;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.XPath;
using Microsoft.PackageGraph.Storage.Local;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    class ContentSync
    {
        public static void SyncContent(ContentSyncOptions options)
        {
            var metadataSource = MetadataStoreCreator.OpenFromOptions(options as IMetadataStoreOptions);
            if (metadataSource == null)
            {
                return;
            }

            var contentStore = GetContentStoreFromOptions(options);
            if (contentStore == null)
            {
                return;
            }

            var filter = FilterBuilder.MicrosoftUpdateFilterFromCommandLine(options as IMetadataFilterOptions);
            if (filter == null)
            {
                return;
            }

            var filteredPackages = filter.Apply(metadataSource);

            var filesToDownload = filteredPackages.Where(p => (!options.EnglishOnly || IsEnglishOrNeutral(metadataSource, p as MicrosoftUpdatePackage)) && p.Files != null).SelectMany(p => p.Files).ToList();

            foreach(var microsoftUpdatePackage in filteredPackages.OfType<MicrosoftUpdatePackage>())
            {
                filesToDownload.AddRange(GetAllUpdateFiles(metadataSource, microsoftUpdatePackage, options.EnglishOnly));
            }

            filesToDownload = filesToDownload.Distinct().ToList();

            Console.WriteLine($"Sync {filesToDownload.Count} files, {filesToDownload.Sum(f => (long)f.Size)} bytes. Continue? (y/n)");
            if (Console.ReadKey().Key != ConsoleKey.Y)
            {
                return;
            }

            CancellationTokenSource cancelTokenSource = new();
            contentStore.Progress += ContentStore_Progress;
            contentStore.Download(filesToDownload, cancelTokenSource.Token);
        }

        /// <summary>
        /// Gets all files for an update, including files in bundled updates (recursive)
        /// </summary>
        /// <param name="update"></param>
        /// <returns></returns>
        private static List<IContentFile> GetAllUpdateFiles(IMetadataStore metadataSource, MicrosoftUpdatePackage update, bool englishOnly)
        {
            var filesList = new List<IContentFile>();
            if (englishOnly && !IsEnglishOrNeutral(metadataSource, update))
            {
                return filesList;
            }
            if (update.Files != null)
            {
                filesList.AddRange(update.Files);
            }

            if (update is SoftwareUpdate softwareUpdate && softwareUpdate.BundledUpdates != null)
            {
                foreach (var bundledUpdate in softwareUpdate.BundledUpdates)
                {
                    try
                    {
                        var bundledPackage = metadataSource.GetPackage(bundledUpdate) as MicrosoftUpdatePackage;
                        if (bundledPackage == null)
                        {
                            Console.Error.WriteLine($"Skipping missing bundled update {bundledUpdate}");
                            continue;
                        }
                        filesList.AddRange(GetAllUpdateFiles(metadataSource, bundledPackage, englishOnly));
                    }
                    catch (KeyNotFoundException)
                    {
                        Console.Error.WriteLine($"Skipping missing bundled update {bundledUpdate}");
                    }
                }
            }

            return filesList;
        }

        private static bool IsEnglishOrNeutral(IMetadataStore metadataSource, MicrosoftUpdatePackage update)
        {
            using var metadataStream = metadataSource.GetMetadata(update.Id);
            var document = new XPathDocument(metadataStream);
            var navigator = document.CreateNavigator();
            var namespaces = new XmlNamespaceManager(navigator.NameTable);
            namespaces.AddNamespace("upd", "http://schemas.microsoft.com/msus/2002/12/Update");
            var language = navigator.SelectSingleNode("/upd:Update/upd:Properties/upd:Language", namespaces)?.Value;
            return string.IsNullOrEmpty(language) || language.Equals("en", StringComparison.OrdinalIgnoreCase) || language.Equals("all", StringComparison.OrdinalIgnoreCase);
        }


        static string ContentSyncLastFileDigest = "";

        private static void UpdateConsoleForMessageRefresh()
        {
            try
            {
                if (!Console.IsOutputRedirected)
                {
                    Console.CursorLeft = 0;
                }
                else
                {
                    Console.WriteLine();
                }
            }
            catch (IOException)
            {
                Console.WriteLine();
            }
            catch (ArgumentOutOfRangeException)
            {
                Console.WriteLine();
            }
        }

        private static void ContentStore_Progress(object sender, ObjectModel.ContentOperationProgress e)
        {
            if (e.File.Digest.DigestBase64 != ContentSyncLastFileDigest)
            {
                Console.WriteLine();
                ContentSyncLastFileDigest = e.File.Digest.DigestBase64;
            }

            switch(e.CurrentOperation)
            {
                case ObjectModel.PackagesOperationType.DownloadFileProgress:
                    UpdateConsoleForMessageRefresh();
                    Console.Write("Sync'ing update content [{0}]: {1:000.00}%", e.Maximum, e.PercentDone);
                    break;
            }
        }

        private static IContentStore GetContentStoreFromOptions(ContentSyncOptions options)
        {
            switch(options.ContentStoreType)
            {
                case "local":
                    return new FileSystemContentStore(options.ContentPath);

                case "azure":
                    if (string.IsNullOrEmpty(options.ContentStoreConnectionString))
                    {
                        ConsoleOutput.WriteRed("Connection string required for Azure stores");
                        return null;
                    }

                    if (!CloudStorageAccount.TryParse(options.ContentStoreConnectionString, out var storageAccount))
                    {
                        ConsoleOutput.WriteRed("Invalid connection string");
                        return null;
                    }

                    var blobClient = storageAccount.CreateCloudBlobClient();
                    return Storage.Azure.BlobContentStore.OpenOrCreate(blobClient, options.ContentPath);

                default:
                    ConsoleOutput.WriteRed("Content store type not supported.");
                    return null;

            }
        }
    }
}
