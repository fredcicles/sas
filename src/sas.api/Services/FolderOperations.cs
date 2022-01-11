﻿using Azure.Identity;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace sas.api.Services
{
    internal class FolderOperations
    {
        private readonly ILogger log;
        private readonly DataLakeFileSystemClient dlfsClient;
        private readonly decimal costPerTB;
        public FolderOperations(Uri storageUri, string fileSystem, ILogger log)
        {
            this.log = log;
            var costPerTB = Environment.GetEnvironmentVariable("COST_PER_TB");
            if (costPerTB != null)
                decimal.TryParse(costPerTB, out this.costPerTB);

            var tenantId = Environment.GetEnvironmentVariable("TENANT_ID");
            var clientId = Environment.GetEnvironmentVariable("APP_REGISTRATION_CLIENT_ID");
            var clientSecret = Environment.GetEnvironmentVariable("CLIENT_SECRET");
            var tokenCred = new ClientSecretCredential(tenantId, clientId, clientSecret);
            dlfsClient = new DataLakeServiceClient(storageUri, tokenCred).GetFileSystemClient(fileSystem);
        }

        public bool CreateNewFolder(string folder, out string error)
        {
            error = null;
            log.LogTrace($"Creating the folder '{folder}' within the container '{dlfsClient.Uri}'...");

            try
            {
                var directoryClient = dlfsClient.GetDirectoryClient(folder);
                var response = directoryClient.Create();
                var statusFlag = response.GetRawResponse().Status == 201;
                if (!statusFlag)
                    error = "Error trying to create the new folder. Error 500.";
                return statusFlag;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal bool AssignFullRwx(string folder, string folderOwner, out string error)
        {
            log.LogTrace($"Assigning RWX permission to Folder Owner ({folderOwner}) at folder's ({folder}) level...");

            var directoryClient = dlfsClient.GetDirectoryClient(folder);

            var accessControlListUpdate = new List<PathAccessControlItem>
            {
                new PathAccessControlItem(
                    accessControlType: AccessControlType.User,
                    permissions: RolePermissions.Read | RolePermissions.Write | RolePermissions.Execute,
                    entityId: folderOwner),
                new PathAccessControlItem(
                    accessControlType: AccessControlType.User,
                    permissions: RolePermissions.Read | RolePermissions.Write | RolePermissions.Execute,
                    entityId: folderOwner,
                    defaultScope: true)
            };

            // Send up changes
            var result = directoryClient.UpdateAccessControlRecursive(accessControlListUpdate);
            var statusFlag = result.GetRawResponse().Status == (int)HttpStatusCode.OK;

            error = statusFlag ? null : "Error trying to assign the RWX permission to the folder. Error 500.";
            return statusFlag;
        }

        internal bool AddFundCodeToMetaData(string folder, string fundCode, out string error)
        {
            log.LogTrace($"Saving FundCode into container's metadata...");
            try
            {
                var directoryClient = dlfsClient.GetDirectoryClient(folder);

                // Check the Last Calculated Date from the Metadata
                var meta = directoryClient.GetProperties().Value.Metadata;

                // Add Fund Code
                meta.Add("FundCode", fundCode);

                // Strip off a readonly item
                meta.Remove("hdi_isfolder");

                // Save back into the Directory Metadata
                directoryClient.SetMetadata(meta);

                error = null;
            }catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            return true;
        }

        internal long CalculateFolderSize(string folder)
        {
            const string sizeCalcDateKey = "SizeCalcDate";
            const string sizeKey = "Size";
            log.LogTrace($"Calculating size for ({dlfsClient.Uri})/({folder})");

            var directoryClient = dlfsClient.GetDirectoryClient(folder);

            // Check the Last Calculated Date from the Metadata
            var meta = directoryClient.GetProperties().Value.Metadata;
            var sizeCalcDate = meta.ContainsKey(sizeCalcDateKey)
                ? DateTime.Parse(meta[sizeCalcDateKey])
                : DateTime.MinValue;

            // If old calculate size again
            if (DateTime.UtcNow.Subtract(sizeCalcDate).TotalDays > 7)
            {
                var paths = directoryClient.GetPaths(true, false);
                long size = 0;
                foreach (var path in paths)
                {
                    size += (path.ContentLength.HasValue) ? (int)path.ContentLength : 0;
                }
                meta[sizeCalcDateKey] = DateTime.UtcNow.ToString();
                meta[sizeKey] = size.ToString();

                // Strip off a readonly item
                meta.Remove("hdi_isfolder");

                // Save back into the Directory Metadata
                directoryClient.SetMetadata(meta);
            }

            return long.Parse(meta[sizeKey]);
        }
        private static string Simplify(string s)
        {
            return s.Replace('@', '_').ToLower();
        }

        internal IEnumerable<FolderDetail> GetAccessibleFolders(string upn)
        {
            // Get all Top Level Folders
            var folders = dlfsClient.GetPaths().Where<PathItem>(
                pi => pi.IsDirectory != null && (bool)pi.IsDirectory)
                .ToList();

            // Translate for guest accounts
            upn = Simplify(upn);

            // Find folders that have ACL entries for upn
            foreach (var folder in folders)
            { 
                var rootClient = dlfsClient.GetDirectoryClient(folder.Name);  // container (root)
                var acl = rootClient.GetAccessControl(userPrincipalName: true);
                if (acl.Value.AccessControlList.Any(
                        p => p.EntityId is not null 
                        && Simplify(p.EntityId).StartsWith(upn)
                        && p.Permissions.HasFlag(RolePermissions.Read)
                    )
                )
                {
                    // Get Metadata
                    var meta = rootClient.GetProperties().Value.Metadata;
                    long? size = meta.ContainsKey("Size") ? long.Parse(meta["Size"]) : null;
                    decimal? cost = (size == null) ? null : size * costPerTB / 1000000000000;

                    // Calculate UserAccess
                    var userAccess = acl.Value.AccessControlList
                        .Where(p => p.AccessControlType == AccessControlType.User && p.EntityId != null && !p.DefaultScope)
                        .Select(p => p.EntityId)
                        .ToList();

                    // Create Folder Details
                    var fd = new FolderDetail()
                    {
                        Name = folder.Name,
                        Size = meta.ContainsKey("Size") ? meta["Size"] : null,
                        Cost = cost.HasValue ? cost.Value.ToString() : null,
                        FundCode = meta.ContainsKey("FundCode") ? meta["FundCode"] : null,
                        UserAccess = userAccess
                    };

                    yield return fd;
                }
            }
        }

        internal class FolderDetail
        {
            public string Name { get; set; }
            public string Size { get; set; }
            public string Cost { get; set; }
            public string FundCode { get; set; }
            public IList<string> UserAccess { get; set; }
        }
    }
}