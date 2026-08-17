// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UpdateServices.WebServices.ServerSync;
using System.Linq;
using System.IO;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.PackageGraph.Storage;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Prerequisites;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content;
using System.Threading;
using Microsoft.PackageGraph.Storage.Local;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Endpoints.ServerSync
{
    /// <summary>
    /// Upstream update server implementation. Provides updates over the ServerSync protocol to downstream servers.
    /// <para>The communication protocol with clients is SOAP</para>
    /// </summary>
    public class ServerSyncWebService : IServerSyncAspNetCore
    {
        private IContentStore ContentStore;

        /// <summary>
        /// The source of upate metadata that this server serves.
        /// </summary>
        private IMetadataStore PackageStore;
        private readonly ReaderWriterLock PackageStoreLock = new();

        /// <summary>
        /// Cached service configuration
        /// </summary>
        private ServerSyncConfigData ServiceConfiguration;
        private readonly ReaderWriterLock ServiceConfigurationLock = new();

        private Dictionary<Guid, List<MicrosoftUpdatePackage>> ProductsIndex;
        private Dictionary<Guid, List<MicrosoftUpdatePackage>> ClassificationsIndex;

        private List<MicrosoftUpdatePackage> Categories;
        private Dictionary<MicrosoftUpdatePackageIdentity, MicrosoftUpdatePackage> Updates;

        /// <summary>
        /// Default constructor
        /// </summary>
        public ServerSyncWebService()
        {
        }

        /// <summary>
        /// ASP.NET extension method for setting service configuration
        /// </summary>
        /// <param name="serviceConfig"></param>
        public void SetServerConfiguration(ServerSyncConfigData serviceConfig)
        {
            ServiceConfigurationLock.AcquireWriterLock(-1);
            ServiceConfiguration = serviceConfig;
            ServiceConfigurationLock.ReleaseWriterLock();
        }

        /// <summary>
        /// Sets the package store for this instance of the server
        /// </summary>
        /// <param name="packageSource">The package store to server updates from</param>
        public void SetPackageStore(IMetadataStore packageSource)
        {
            PackageStoreLock.AcquireWriterLock(-1);

            PackageStore = packageSource;

            Categories = new List<MicrosoftUpdatePackage>();
            Updates = new Dictionary<MicrosoftUpdatePackageIdentity, MicrosoftUpdatePackage>();
            ProductsIndex = new Dictionary<Guid, List<MicrosoftUpdatePackage>>();
            ClassificationsIndex = new Dictionary<Guid, List<MicrosoftUpdatePackage>>();

            if (PackageStore != null)
            {
                Categories.AddRange(packageSource.OfType<ProductCategory>());
                Categories.AddRange(packageSource.OfType<ClassificationCategory>());
                Categories.AddRange(packageSource.OfType<DetectoidCategory>());
                foreach(var softwarePackage in packageSource.OfType<SoftwareUpdate>())
                {
                    Updates.Add(softwarePackage.Id as MicrosoftUpdatePackageIdentity, softwarePackage);
                }

                foreach (var driverUpdate in packageSource.OfType<DriverUpdate>())
                {
                    Updates.Add(driverUpdate.Id as MicrosoftUpdatePackageIdentity, driverUpdate);
                }

                foreach (var classification in Categories.OfType<ClassificationCategory>())
                {
                    ClassificationsIndex.TryAdd(classification.Id.ID, new List<MicrosoftUpdatePackage>());
                }

                foreach (var product in Categories.OfType<ProductCategory>())
                {
                    ProductsIndex.TryAdd(product.Id.ID, new List<MicrosoftUpdatePackage>());
                }

                foreach (var update in Updates.Values)
                {
                    if (update.Prerequisites != null)
                    {
                        foreach (var prerequisite in update.Prerequisites.OfType<AtLeastOne>().SelectMany(p => p.Simple))
                        {
                            if (ClassificationsIndex.ContainsKey(prerequisite.UpdateId))
                            {
                                ClassificationsIndex[prerequisite.UpdateId].Add(update);
                            }

                            if (ProductsIndex.ContainsKey(prerequisite.UpdateId))
                            {
                                ProductsIndex[prerequisite.UpdateId].Add(update);
                            }
                        }

                        foreach (var prerequisite in update.Prerequisites.OfType<Simple>())
                        {
                            if (ClassificationsIndex.ContainsKey(prerequisite.UpdateId))
                            {
                                ClassificationsIndex[prerequisite.UpdateId].Add(update);
                            }

                            if (ProductsIndex.ContainsKey(prerequisite.UpdateId))
                            {
                                ProductsIndex[prerequisite.UpdateId].Add(update);
                            }
                        }
                    }
                }
            }

            PackageStoreLock.ReleaseWriterLock();
        }

        /// <summary>
        /// Sets the content store used to determine whether advertised updates
        /// can actually be served to a downstream WSUS server.
        /// </summary>
        public void SetContentStore(IContentStore contentStore)
        {
            ContentStore = contentStore;
        }

        private bool HasServableContent(MicrosoftUpdatePackageIdentity identity, HashSet<MicrosoftUpdatePackageIdentity> checkedUpdates)
        {
            if (ContentStore == null)
            {
                return true;
            }

            if (!checkedUpdates.Add(identity))
            {
                return true;
            }

            if (!PackageStore.ContainsPackage(identity))
            {
                Console.Error.WriteLine("Skipping update with missing metadata: " + identity);
                return false;
            }

            var update = PackageStore.GetPackage(identity) as MicrosoftUpdatePackage;
            if (update == null)
            {
                return false;
            }

            if (update.Files != null)
            {
                foreach (var file in update.Files)
                {
                    if (file == null || file.Digest == null || !ContentStore.Contains(file.Digest, out _))
                    {
                        Console.Error.WriteLine("Skipping update with missing content: " + identity);
                        return false;
                    }
                }
            }

            if (update is SoftwareUpdate softwareUpdate && softwareUpdate.BundledUpdates != null)
            {
                foreach (var bundledUpdate in softwareUpdate.BundledUpdates)
                {
                    if (!HasServableContent(bundledUpdate, checkedUpdates))
                    {
                        Console.Error.WriteLine("Skipping update with incomplete bundled content: " + identity);
                        return false;
                    }
                }
            }

            return true;
        }

        private bool IsServablePrerequisite(XmlNode node)
        {
            if (!Guid.TryParse(node.Attributes["UpdateID"]?.Value, out var prerequisiteId))
            {
                return false;
            }

            // WSUS 2.0 validates prerequisite identities against the
            // revisions imported in the same synchronization. Update-package
            // prerequisites are not reliably present in the filtered revision
            // list, so retaining them produces Event 386. The legacy WSUS
            // client can evaluate the applicability rules without these
            // optional prerequisite groups, while retaining them makes the
            // whole metadata import fail.
            return false;
        }

        private string SanitizePrerequisites(string xml, MicrosoftUpdatePackageIdentity identity)
        {
            var document = new XmlDocument();
            document.LoadXml(xml);
            var namespaces = new XmlNamespaceManager(document.NameTable);
            namespaces.AddNamespace("upd", "http://schemas.microsoft.com/msus/2002/12/Update");

            var simpleNodes = document.SelectNodes("/upd:Update/upd:Relationships/upd:Prerequisites/upd:UpdateIdentity", namespaces);
            foreach (XmlNode node in simpleNodes)
            {
                if (!IsServablePrerequisite(node))
                {
                    node.ParentNode.RemoveChild(node);
                    Console.Error.WriteLine("Removed unavailable prerequisite from update " + identity);
                }
            }

            var atLeastOneNodes = document.SelectNodes("/upd:Update/upd:Relationships/upd:Prerequisites/upd:AtLeastOne", namespaces);
            foreach (XmlNode group in atLeastOneNodes)
            {
                var identities = group.SelectNodes("upd:UpdateIdentity", namespaces);
                foreach (XmlNode node in identities)
                {
                    if (!IsServablePrerequisite(node))
                    {
                        group.RemoveChild(node);
                        Console.Error.WriteLine("Removed unavailable AtLeastOne prerequisite from update " + identity);
                    }
                }

                if (group.SelectNodes("upd:UpdateIdentity", namespaces).Count == 0)
                {
                    group.ParentNode.RemoveChild(group);
                }
            }

            var prerequisitesElement = document.SelectSingleNode(
                "/upd:Update/upd:Relationships/upd:Prerequisites",
                namespaces);
            if (prerequisitesElement != null && !prerequisitesElement.HasChildNodes)
            {
                prerequisitesElement.ParentNode.RemoveChild(prerequisitesElement);
            }

            var supersededNodes = document.SelectNodes(
                "/upd:Update/upd:Relationships/upd:SupersededUpdates//upd:UpdateIdentity",
                namespaces);
            foreach (XmlNode node in supersededNodes)
            {
                // WSUS 2.0 requires a revision number for superseded-update
                // identities. Legacy metadata often supplies only UpdateID.
                if (node.Attributes["RevisionNumber"] == null)
                {
                    node.ParentNode.RemoveChild(node);
                    Console.Error.WriteLine("Removed unresolved superseded update from update " + identity);
                }
            }

            var supersededElement = document.SelectSingleNode(
                "/upd:Update/upd:Relationships/upd:SupersededUpdates",
                namespaces);
            if (supersededElement != null && !supersededElement.HasChildNodes)
            {
                supersededElement.ParentNode.RemoveChild(supersededElement);
            }

            var localizedProperties = document.SelectNodes(
                "/upd:Update/upd:LocalizedPropertiesCollection/upd:LocalizedProperties",
                namespaces);
            foreach (XmlNode localizedProperty in localizedProperties)
            {
                var language = localizedProperty.SelectSingleNode("upd:Language", namespaces)?.InnerText;
                if (!string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
                {
                    localizedProperty.ParentNode.RemoveChild(localizedProperty);
                }
            }

            var localizedPropertiesElement = document.SelectSingleNode(
                "/upd:Update/upd:LocalizedPropertiesCollection",
                namespaces);
            if (localizedPropertiesElement != null && !localizedPropertiesElement.HasChildNodes)
            {
                localizedPropertiesElement.ParentNode.RemoveChild(localizedPropertiesElement);
            }

            return document.OuterXml;
        }

        private List<MicrosoftUpdatePackageIdentity> OrderBundledUpdatesFirst(IEnumerable<MicrosoftUpdatePackageIdentity> identities)
        {
            var candidates = new HashSet<MicrosoftUpdatePackageIdentity>(identities);
            var ordered = new List<MicrosoftUpdatePackageIdentity>();
            var visited = new HashSet<MicrosoftUpdatePackageIdentity>();

            Action<MicrosoftUpdatePackageIdentity> visit = null;
            visit = identity =>
            {
                if (!candidates.Contains(identity) || !visited.Add(identity))
                {
                    return;
                }

                if (Updates.TryGetValue(identity, out var update) && update is SoftwareUpdate softwareUpdate && softwareUpdate.BundledUpdates != null)
                {
                    foreach (var bundledUpdate in softwareUpdate.BundledUpdates)
                    {
                        visit(bundledUpdate);
                    }
                }

                if (Updates.TryGetValue(identity, out update) && update.Prerequisites != null)
                {
                    foreach (var prerequisite in update.Prerequisites)
                    {
                        var prerequisites = prerequisite is AtLeastOne atLeastOne
                            ? atLeastOne.Simple
                            : new List<Simple> { (Simple)prerequisite };

                        foreach (var simplePrerequisite in prerequisites)
                        {
                            var prerequisiteIdentity = candidates.FirstOrDefault(candidate => candidate.ID == simplePrerequisite.UpdateId);
                            if (prerequisiteIdentity != null)
                            {
                                visit(prerequisiteIdentity);
                            }
                        }
                    }
                }

                ordered.Add(identity);
            };

            foreach (var identity in candidates)
            {
                visit(identity);
            }

            return ordered;
        }

        private IEnumerable<Guid> ReadPrerequisiteIds(MicrosoftUpdatePackageIdentity identity)
        {
            var document = new XmlDocument();
            using (var metadata = PackageStore.GetMetadata(identity))
            {
                document.Load(metadata);
            }

            var namespaces = new XmlNamespaceManager(document.NameTable);
            namespaces.AddNamespace("upd", "http://schemas.microsoft.com/msus/2002/12/Update");
            var result = document.SelectNodes(
                    "/upd:Update/upd:Relationships/upd:Prerequisites//upd:UpdateIdentity/@UpdateID",
                    namespaces)
                .Cast<XmlAttribute>()
                .Select(attribute => Guid.Parse(attribute.Value))
                .Distinct()
                .ToList();
            return result;
        }

        // Prerequisites are represented in update XML by UpdateID only. They
        // are not necessarily selected by the product/classification filter,
        // but WSUS must have them in SUSDB before it imports dependent XML.
        private bool AddPrerequisiteClosure(
            MicrosoftUpdatePackageIdentity identity,
            HashSet<MicrosoftUpdatePackageIdentity> included,
            HashSet<MicrosoftUpdatePackageIdentity> visiting)
        {
            if (included.Contains(identity))
            {
                return true;
            }

            if (!visiting.Add(identity))
            {
                return true;
            }

            if (!Updates.TryGetValue(identity, out var update) ||
                !HasServableContent(identity, new HashSet<MicrosoftUpdatePackageIdentity>()))
            {
                visiting.Remove(identity);
                return false;
            }

            foreach (var prerequisiteId in ReadPrerequisiteIds(identity))
            {
                // Categories are already advertised by GetConfig and
                // are not update revisions that can be added here.
                if (Categories.Any(category => category.Id.ID == prerequisiteId))
                {
                    continue;
                }

                var prerequisiteRevisions = Updates
                    .Where(pair => pair.Key.ID == prerequisiteId)
                    .Select(pair => pair.Key)
                    .Where(candidate => HasServableContent(candidate, new HashSet<MicrosoftUpdatePackageIdentity>()))
                    .ToList();

                // Returning this update would leave an UpdateIdentity
                // in its XML that WSUS cannot resolve.
                if (prerequisiteRevisions.Count == 0)
                {
                    Console.Error.WriteLine("Skipping update with unavailable prerequisite " + prerequisiteId + ": " + identity);
                    visiting.Remove(identity);
                    return false;
                }

                foreach (var prerequisiteRevision in prerequisiteRevisions)
                {
                    if (!AddPrerequisiteClosure(prerequisiteRevision, included, visiting))
                    {
                        visiting.Remove(identity);
                        return false;
                    }
                }
            }

            visiting.Remove(identity);
            included.Add(identity);
            return true;
        }

        private List<MicrosoftUpdatePackageIdentity> AddPrerequisiteUpdates(IEnumerable<MicrosoftUpdatePackageIdentity> identities)
        {
            var included = new HashSet<MicrosoftUpdatePackageIdentity>();
            foreach (var identity in identities)
            {
                AddPrerequisiteClosure(identity, included, new HashSet<MicrosoftUpdatePackageIdentity>());
            }

            return included.ToList();
        }

        private static string NormalizeLegacyDateTimes(string xml)
        {
            return Regex.Replace(xml, @"\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})\b", match =>
            {
                if (!DateTimeOffset.TryParse(match.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
                {
                    return match.Value;
                }

                return value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            });
        }

        /// <summary>
        /// Handle authentication data requests
        /// </summary>
        /// <param name="request">The request data. Not used</param>
        /// <returns>Exactly one canned authentication method</returns>
        public Task<ServerAuthConfig> GetAuthConfigAsync(GetAuthConfigRequest request)
        {
            // Build the standard response
            var result = new ServerAuthConfig()
            {
                LastChange = DateTime.Now,
                AuthInfo = new AuthPlugInInfo[]
                {
                    new AuthPlugInInfo() { PlugInID = "DssTargeting", ServiceUrl = "DssAuthWebService/DssAuthWebService.asmx" } 
                }
            };

            GetAuthConfigResponse response = new(
                new GetAuthConfigResponseBody() 
                { 
                    GetAuthConfigResult = result 
                });

            return Task.FromResult(response.GetAuthConfigResponse1.GetAuthConfigResult);
        }

        /// <summary>
        /// Handle service configuration requests
        /// </summary>
        /// <param name="request">Service configuration request</param>
        /// <returns>Returns the cached service configuration of the upstream server the local repo is tracking</returns>
        public Task<ServerSyncConfigData> GetConfigDataAsync(GetConfigDataRequest request)
        {
            ServerSyncConfigData capturedConfigData;
            ServiceConfigurationLock.AcquireReaderLock(-1);
            capturedConfigData = ServiceConfiguration;
            ServiceConfigurationLock.ReleaseReaderLock();
            return Task.FromResult(capturedConfigData);
        }

        /// <summary>
        /// Handle request for a cookie
        /// </summary>
        /// <param name="request">Cookie request. Not used; all requests are granted</param>
        /// <returns>A cookie that expires in 5 days.</returns>
        public Task<Cookie> GetCookieAsync(GetCookieRequest request)
        {
            return Task.FromResult(new Cookie() { Expiration = DateTime.Now.AddDays(5), EncryptedData = new byte[12] });
        }

        /// <summary>
        /// Return a list of update ids
        /// </summary>
        /// <param name="request">Request data. Can specify categories or updates, filters, etc.</param>
        /// <returns></returns>
        public Task<RevisionIdList> GetRevisionIdListAsync(GetRevisionIdListRequest request)
        {
            var response = new RevisionIdList
            {
                Anchor = DateTime.Now.ToString()
            };

            PackageStoreLock.AcquireReaderLock(-1);

            try
            {
                if (request.GetRevisionIdList.filter.GetConfig == true)
                {
                    response.NewRevisions = Categories.Select(u => new UpdateIdentity() { UpdateID = u.Id.ID, RevisionNumber = u.Id.Revision }).ToArray();
                }
                else
                {
                    var productsFilter = request.GetRevisionIdList.filter.Categories;
                    var classificationsFilter = request.GetRevisionIdList.filter.Classifications;

                    var effectiveProductsFilter = productsFilter == null ? ProductsIndex.Keys : productsFilter.Select(p => p.Id);
                    var effectiveClassificationsFilter = classificationsFilter == null ? ClassificationsIndex.Keys : classificationsFilter.Select(c => c.Id);

                    var filteredByProduct = new List<MicrosoftUpdatePackageIdentity>();
                    foreach (var product in effectiveProductsFilter)
                    {
                        if (ProductsIndex.ContainsKey(product))
                        {
                            filteredByProduct.AddRange(ProductsIndex[product].Select(u => u.Id));
                        }
                    }

                    var filteredByClassification = new List<MicrosoftUpdatePackageIdentity>();
                    foreach (var classification in effectiveClassificationsFilter)
                    {
                        if (ClassificationsIndex.ContainsKey(classification))
                        {
                            filteredByClassification.AddRange(ClassificationsIndex[classification].Select(u => u.Id));
                        }
                    }

                    // Also select all updates that are bundled with updates matching the filter
                    var filteredResult = filteredByProduct.Intersect(filteredByClassification);
                    List<MicrosoftUpdatePackageIdentity> bundledUpdates = new();
                    foreach(var result in filteredResult)
                    {
                        if (Updates[result] is SoftwareUpdate softwareUpdate)
                        {
                            if (softwareUpdate.BundledUpdates != null)
                            {
                                bundledUpdates.AddRange(softwareUpdate.BundledUpdates);
                            }
                        }
                    }
                    
                    // Deduplicate result and convert to raw identity format
                    var advertisedUpdates = filteredResult
                        .Union(bundledUpdates)
                                        .Distinct()
                        .Where(u => PackageStore.ContainsPackage(u))
                        .Where(u => HasServableContent(u, new HashSet<MicrosoftUpdatePackageIdentity>()))
                        .ToList();

                    var withPrerequisites = AddPrerequisiteUpdates(advertisedUpdates);
                    response.NewRevisions = OrderBundledUpdatesFirst(withPrerequisites)
                        .Select(u => new UpdateIdentity() { UpdateID = u.ID, RevisionNumber = u.Revision })
                        .ToArray();
                }
            }
            catch(Exception ex)
            {
                Console.Error.WriteLine("GetRevisionIdList failed: " + ex.Message);
            }

            PackageStoreLock.ReleaseReaderLock();

            return Task.FromResult(response);
        }

        /// <summary>
        /// Return metadata for updates
        /// </summary>
        /// <param name="request">The request; contains IDs for updates to retrieve metadata for</param>
        /// <returns>Update metadata for requested updates</returns>
        public Task<ServerUpdateData> GetUpdateDataAsync(GetUpdateDataRequest request)
        {
            var response = new ServerUpdateData();

            ServerSyncConfigData serviceConfiguration;
            ServiceConfigurationLock.AcquireReaderLock(-1);
            serviceConfiguration = ServiceConfiguration;
            ServiceConfigurationLock.ReleaseReaderLock();

            if (serviceConfiguration == null)
            {
                return Task.FromResult(response);
            }

            if (PackageStore == null)
            {
                return Task.FromResult(response);
            }

            // Make sure the request is not larger than the config says
            var updateRequestCount = request.GetUpdateData.updateIds.Length;
            Console.WriteLine("GetUpdateData: requested " + updateRequestCount + " updates; first=" + request.GetUpdateData.updateIds.FirstOrDefault()?.UpdateID + "/" + request.GetUpdateData.updateIds.FirstOrDefault()?.RevisionNumber);
            if (updateRequestCount > serviceConfiguration.MaxNumberOfUpdatesPerRequest)
            {
                return null;
            }

            PackageStoreLock.AcquireReaderLock(-1);

            var returnUpdatesList = new List<ServerSyncUpdateData>();
            var returnFilesList = new List<ServerSyncUrlData>();
            var checkedUpdates = new HashSet<MicrosoftUpdatePackageIdentity>();

            try
            {
                foreach (var rawIdentity in request.GetUpdateData.updateIds)
                {
                    var updateIdentity = new MicrosoftUpdatePackageIdentity(rawIdentity.UpdateID, rawIdentity.RevisionNumber);

                    if (!PackageStore.ContainsPackage(updateIdentity))
                    {
                        Console.Error.WriteLine("GetUpdateData missing package: " + rawIdentity.UpdateID + "/" + rawIdentity.RevisionNumber + "; returned " + returnUpdatesList.Count + " of " + updateRequestCount);
                        throw new Exception("Update not found");
                    }

                    var update = PackageStore.GetPackage(updateIdentity) as MicrosoftUpdatePackage;
                    if (update.Files != null)
                    {
                        // if update contains files, we must also gather file information
                        foreach (var updateFile in update.Files)
                        {
                            var microsoftUpdateFile = updateFile as UpdateFile;
                            returnFilesList.Add(
                                new ServerSyncUrlData()
                                {
                                    FileDigest = Convert.FromBase64String(updateFile.Digest.DigestBase64),
                                    MUUrl = microsoftUpdateFile.Urls[0].MuUrl,
                                    UssUrl = $"microsoftupdate/content/{microsoftUpdateFile.Digest.HexString}"
                                });
                        }
                    }

                    var rawUpdateData = new ServerSyncUpdateData
                    {
                        Id = rawIdentity
                    };

                    using (var metadataReader = new StreamReader(PackageStore.GetMetadata(update.Id), Encoding.Unicode))
                    {
                        var metadataXml = metadataReader.ReadToEnd();
                        metadataXml = SanitizePrerequisites(metadataXml, updateIdentity);
                        rawUpdateData.XmlUpdateBlob = NormalizeLegacyDateTimes(metadataXml);
                    }

                    returnUpdatesList.Add(rawUpdateData);
                }
            }
            catch(Exception ex)
            {
                Console.Error.WriteLine("GetUpdateData failed after " + returnUpdatesList.Count + " of " + updateRequestCount + ": " + ex.GetType().Name + ": " + ex.Message);
            }

            response.updates = returnUpdatesList.ToArray();
            // Deduplicate list of files
            response.fileUrls = returnFilesList.GroupBy(f => f.MUUrl).Select(k => k.First()).ToArray();

            PackageStoreLock.ReleaseReaderLock();

            return Task.FromResult(response);
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Task<GetUpdateDecryptionDataResponse> GetUpdateDecryptionDataAsync(GetUpdateDecryptionDataRequest request)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Task<PingResponse> PingAsync(PingRequest request)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Task<GetDeploymentsResponse> GetDeploymentsAsync(GetDeploymentsRequest request)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Task<GetDriverIdListResponse> GetDriverIdListAsync(GetDriverIdListRequest request)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Task<GetDriverSetDataResponse> GetDriverSetDataAsync(GetDriverSetDataRequest request)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Task<GetRelatedRevisionsForUpdatesResponse> GetRelatedRevisionsForUpdatesAsync(GetRelatedRevisionsForUpdatesRequest request)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Task<DownloadFilesResponse> DownloadFilesAsync(DownloadFilesRequest request)
        {
            return Task.FromResult(new DownloadFilesResponse { DownloadFilesResponse1 = new DownloadFilesResponseBody() });
        }
    }
}
