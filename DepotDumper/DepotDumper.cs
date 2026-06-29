// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;
using static SteamKit2.Internal.CContentBuilder_CommitAppBuild_Request;

namespace DepotDumper
{
    class DepotDumperException(string value) : Exception(value)
    {
    }

    static class DepotDumper
    {
        public const uint INVALID_APP_ID = uint.MaxValue;
        public const uint INVALID_DEPOT_ID = uint.MaxValue;
        public const ulong INVALID_MANIFEST_ID = ulong.MaxValue;
        public const string DEFAULT_BRANCH = "public";

        public static DumpConfig Config = new();

        private static Steam3Session steam3;
        private static CDNClientPool cdnPool;

        private const string DEFAULT_DUMP_DIR = "dumps";
        private const string CONFIG_DIR = ".DepotDumper";

        private sealed class DepotDumpInfo(
            uint depotid,
            uint appId,
            ulong manifestId,
            string branch,
            string DumpDir,
            byte[] depotKey)
        {
            public uint DepotId { get; } = depotid;
            public uint AppId { get; } = appId;
            public ulong ManifestId { get; } = manifestId;
            public string Branch { get; } = branch;
            public string DumpDir { get; } = DumpDir;
            public byte[] DepotKey { get; } = depotKey;
        }

        static async Task<bool> AccountHasAccess(uint appId, uint depotId)
        {
            if (steam3 == null || steam3.steamUser.SteamID == null || (steam3.Licenses == null &&
                                                                       steam3.steamUser.SteamID.AccountType !=
                                                                       EAccountType.AnonUser))
                return false;

            IEnumerable<uint> licenseQuery;
            if (steam3.steamUser.SteamID.AccountType == EAccountType.AnonUser)
            {
                licenseQuery = [17906];
            }
            else
            {
                licenseQuery = steam3.Licenses.Select(x => x.PackageID).Distinct();
            }

            await steam3.RequestPackageInfo(licenseQuery);

            foreach (var license in licenseQuery)
            {
                if (steam3.PackageInfo.TryGetValue(license, out var package) && package != null)
                {
                    if (package.KeyValues["appids"].Children.Any(child => child.AsUnsignedInteger() == depotId))
                        return true;

                    if (package.KeyValues["depotids"].Children.Any(child => child.AsUnsignedInteger() == depotId))
                        return true;
                }
            }

            return false;
        }

        internal static KeyValue GetSteam3AppSection(uint appId, EAppInfoSection section)
        {
            if (steam3 == null || steam3.AppInfo == null)
            {
                return null;
            }

            if (!steam3.AppInfo.TryGetValue(appId, out var app) || app == null)
            {
                return null;
            }

            var appinfo = app.KeyValues;
            var section_key = section switch
            {
                EAppInfoSection.Common => "common",
                EAppInfoSection.Extended => "extended",
                EAppInfoSection.Config => "config",
                EAppInfoSection.Depots => "depots",
                _ => throw new NotImplementedException(),
            };
            var section_kv = appinfo.Children.Where(c => c.Name == section_key).FirstOrDefault();
            return section_kv;
        }

        static async Task<ulong> GetSteam3DepotManifest(uint depotId, uint appId, string branch)
        {
            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            var depotChild = depots[depotId.ToString()];

            if (depotChild == KeyValue.Invalid)
                return INVALID_MANIFEST_ID;

            // Shared depots can either provide manifests, or leave you relying on their parent app.
            // It seems that with the latter, "sharedDump" will exist (and equals 2 in the one existance I know of).
            // Rather than relay on the unknown sharedDump key, just look for manifests. Test cases: 111710, 346680.
            if (depotChild["manifests"] == KeyValue.Invalid && depotChild["depotfromapp"] != KeyValue.Invalid)
            {
                var otherAppId = depotChild["depotfromapp"].AsUnsignedInteger();
                if (otherAppId == appId)
                {
                    // This shouldn't ever happen, but ya never know with Valve. Don't infinite loop.
                    Console.WriteLine("App {0}, Depot {1} has depotfromapp of {2}!",
                        appId, depotId, otherAppId);
                    return INVALID_MANIFEST_ID;
                }

                await steam3.RequestAppInfo(otherAppId);

                return await GetSteam3DepotManifest(depotId, otherAppId, branch);
            }

            var manifests = depotChild["manifests"];
            var manifests_encrypted = depotChild["encryptedmanifests"];

            if (manifests.Children.Count == 0 && manifests_encrypted.Children.Count == 0)
                return INVALID_MANIFEST_ID;

            var node = manifests[branch]["gid"];

            if (node == KeyValue.Invalid && !string.Equals(branch, DEFAULT_BRANCH, StringComparison.OrdinalIgnoreCase))
            {
                var node_encrypted = manifests_encrypted[branch];
                if (node_encrypted != KeyValue.Invalid)
                {
                    var password = "";
                    while (string.IsNullOrEmpty(password))
                    {
                        Console.Write("Please enter the password for branch {0}: ", branch);
                        password = Console.ReadLine();
                    }

                    var encrypted_gid = node_encrypted["gid"];

                    if (encrypted_gid != KeyValue.Invalid)
                    {
                        // Submit the password to Steam now to get encryption keys
                        await steam3.CheckAppBetaPassword(appId, password);

                        if (!steam3.AppBetaPasswords.TryGetValue(branch, out var appBetaPassword))
                        {
                            Console.WriteLine("Password was invalid for branch {0}", branch);
                            return INVALID_MANIFEST_ID;
                        }

                        var input = Util.DecodeHexString(encrypted_gid.Value);
                        byte[] manifest_bytes;
                        try
                        {
                            manifest_bytes = Util.SymmetricDecryptECB(input, appBetaPassword);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Failed to decrypt branch {0}: {1}", branch, e.Message);
                            return INVALID_MANIFEST_ID;
                        }

                        return BitConverter.ToUInt64(manifest_bytes, 0);
                    }

                    Console.WriteLine("Unhandled depot encryption for depotId {0}", depotId);
                    return INVALID_MANIFEST_ID;
                }

                return INVALID_MANIFEST_ID;
            }

            if (node.Value == null)
                return INVALID_MANIFEST_ID;

            return ulong.Parse(node.Value);
        }

        static string GetAppName(uint appId)
        {
            var info = GetSteam3AppSection(appId, EAppInfoSection.Common);
            if (info == null)
                return string.Empty;

            return info["name"].AsString();
        }

        public static bool InitializeSteam3(string username, string password)
        {
            string loginToken = null;

            if (username != null && Config.RememberPassword)
            {
                _ = AccountSettingsStore.Instance.LoginTokens.TryGetValue(username, out loginToken);
            }

            steam3 = new Steam3Session(
                new SteamUser.LogOnDetails
                {
                    Username = username,
                    Password = loginToken == null ? password : null,
                    ShouldRememberPassword = Config.RememberPassword,
                    AccessToken = loginToken,
                    LoginID = Config.LoginID ?? 0x534B32, // "SK2"
                }
            );

            if (!steam3.WaitForCredentials())
            {
                Console.WriteLine("Unable to get steam3 credentials.");
                return false;
            }

            Task.Run(steam3.TickCallbacks);

            return true;
        }

        public static void ShutdownSteam3()
        {
            if (cdnPool != null)
            {
                cdnPool.Shutdown();
                cdnPool = null;
            }

            if (steam3 == null)
                return;

            steam3.Disconnect();
        }

        public static async Task DumpAppAsync()
        {
            // Load our configuration data containing the depots currently Dumped
            var dumpPath = Config.DumpDirectory;
            if (string.IsNullOrWhiteSpace(dumpPath))
            {
                dumpPath = DEFAULT_DUMP_DIR;
            }

            Directory.CreateDirectory(Path.Combine(dumpPath, CONFIG_DIR));


            Console.WriteLine("Getting licenses...");

            steam3.WaitUntilCallback(() => { }, () => { return steam3.Licenses != null; });
            IEnumerable<uint> licenseQuery;
            licenseQuery = steam3.Licenses.Select(x => x.PackageID).Distinct();
            await steam3.RequestPackageInfo(licenseQuery);
            HashSet<uint> freeIds = [];
            if (steam3.PackageInfo.TryGetValue(0, out var freepackage))
            {
                freeIds.UnionWith(freepackage.KeyValues["appids"].Children.Select(x => x.AsUnsignedInteger()));
                freeIds.UnionWith(freepackage.KeyValues["depotids"].Children.Select(x => x.AsUnsignedInteger()));
            }

            SortedSet<uint> accountApps = [];
            SortedSet<uint> accountDepots = [];
            SortedSet<uint> accountAccess = [];
            foreach (var license in licenseQuery)
            {
                if (license == 0)
                {
                    continue;
                }

                SteamApps.PICSProductInfoCallback.PICSProductInfo package;
                if (steam3.PackageInfo.TryGetValue(license, out package) && package != null)
                {
                    accountApps.UnionWith(package.KeyValues["appids"].Children.Select(x => x.AsUnsignedInteger()).Where(r => !freeIds.Contains(r)));
                    accountDepots.UnionWith(package.KeyValues["depotids"].Children.Select(x => x.AsUnsignedInteger()).Where(r => !freeIds.Contains(r)));
                }
            }
            var appTokens = await steam3.steamApps.PICSGetAccessTokens(accountApps.ToArray(), []);
            var appReq = appTokens.AppTokens.Select(r => new SteamApps.PICSRequest(r.Key, r.Value)).ToArray();
            Console.WriteLine($"Request AppInfo {appReq.Length}...");
            var appInfoMultiple = await steam3.steamApps.PICSGetProductInfo(appReq, []);
            SortedDictionary<uint, (string, ulong)> AppTokens = [];
            SortedDictionary<uint, string> DepotKeys = [];
            foreach (var appInfo in appInfoMultiple.Results)
            {
                var count = appInfo.Apps.Count;
                foreach ((int i, var app_value) in appInfo.Apps.Index())
                {
                    var app = app_value.Value;
                    //Console.WriteLine($"[{i + 1}/{count}]Got AppInfo for {app.ID}");
                    steam3.AppInfo[app.ID] = app;
                    AppTokens[app.ID] = (GetAppName(app.ID), appTokens.AppTokens.GetValueOrDefault(app.ID, 0UL));
                }
            }
            File.WriteAllLines(Path.Combine(dumpPath, $"{steam3.logonDetails.Username}.appkeys"), AppTokens.Select(r => $"{r.Key};{r.Value.Item1};{r.Value.Item2}"));
            Console.WriteLine("Dump appkeys done.");
            accountAccess.UnionWith(accountApps);
            accountAccess.UnionWith(accountDepots);
            foreach (var (appId, info) in steam3.AppInfo)
            {
                if (freeIds.Contains(appId)) continue;
                await steam3.RequestDepotKey(appId, appId);
                var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
                if (depots == null || depots == KeyValue.Invalid) continue;
                foreach (var depotSection in depots.Children)
                {
                    uint depotId = uint.MaxValue;

                    if (!uint.TryParse(depotSection.Name, out depotId) || depotId == uint.MaxValue)
                        continue;

                    if (depotSection.Children.Count == 0)
                        continue;

                    if (!accountAccess.Contains(depotId))
                        continue;
                    await steam3.RequestDepotKey(depotId, appId);
                }
            }
            File.WriteAllLines(Path.Combine(dumpPath, $"{steam3.logonDetails.Username}.depotkeys"), new SortedDictionary<uint, byte[]>(steam3.DepotKeys).Select(r => $"{r.Key};{Convert.ToHexStringLower(r.Value)}"));
            Console.WriteLine("Dump depotkeys done.");
        }

        static async Task DumpDepot(uint depotId, uint appId, string path)
        {
            try
            {
                cdnPool = new CDNClientPool(steam3, appId);

                if (steam3 != null && appId != INVALID_APP_ID)
                {
                    await steam3.RequestAppInfo(appId);
                }

                /*
                if (!await AccountHasAccess(depotId))
                {
                    Console.WriteLine("Depot {0} is not available from this account.", depotId);

                    return null;
                }
                */
                await steam3.RequestDepotKey(depotId, appId);
                if (steam3.DepotKeys.TryGetValue(depotId, out var depotKey))
                {
                    File.AppendAllText(Path.Combine(path, appId.ToString(), $"{appId.ToString()}.key"),
                        $"{depotId};{string.Concat(depotKey.Select(b => b.ToString("X2")).ToArray())}\n");
                    Console.WriteLine("Depot {0} key: {1}", depotId,
                        string.Concat(depotKey.Select(b => b.ToString("X2")).ToArray()));
                }
                else
                {
                    Console.WriteLine("No valid depot key for {0}.", depotId);
                    return;
                }

                var manifestId = await GetSteam3DepotManifest(depotId, appId, DEFAULT_BRANCH);

                if (manifestId == INVALID_MANIFEST_ID)
                {
                    Console.WriteLine("Depot {0} missing public subsection or manifest section.", depotId);
                    return;
                }

                Console.WriteLine($"Downloading depot {depotId} manifest");

                ulong manifestRequestCode = 0;
                var manifestRequestCodeExpiration = DateTime.MinValue;
                var cts = new CancellationTokenSource();

                Server connection = null;

                connection = cdnPool.GetConnection(cts.Token);

                int retryCount = 0;
                const int maxRetries = 3;
                DepotManifest manifest = null;
                while (retryCount < maxRetries)
                {
                    try
                    {
                        cts.Token.ThrowIfCancellationRequested();

                        string cdnToken = null;
                        if (steam3.CDNAuthTokens.TryGetValue((depotId, connection.Host),
                                out var authTokenCallbackPromise))
                        {
                            var result = await authTokenCallbackPromise.Task;
                            cdnToken = result.Token;
                        }

                        var now = DateTime.Now;

                        // In order to download this manifest, we need the current manifest request code
                        // The manifest request code is only valid for a specific period in time
                        if (manifestRequestCode == 0 || now >= manifestRequestCodeExpiration)
                        {
                            manifestRequestCode = await steam3.GetDepotManifestRequestCodeAsync(
                                depotId,
                                appId,
                                manifestId,
                                DEFAULT_BRANCH);
                            // This code will hopefully be valid for one period following the issuing period
                            manifestRequestCodeExpiration = now.Add(TimeSpan.FromMinutes(5));

                            // If we could not get the manifest code, this is a fatal error
                            if (manifestRequestCode == 0)
                            {
                                cts.Cancel();
                            }
                        }

                        Console.WriteLine("Downloading manifest {0} from {1} with {2}",
                            manifestId,
                            connection,
                            cdnPool.ProxyServer != null ? cdnPool.ProxyServer : "no proxy");

                        manifest = await cdnPool.CDNClient.DownloadManifestAsync(
                            depotId,
                            manifestId,
                            manifestRequestCode,
                            connection,
                            depotKey,
                            cdnPool.ProxyServer,
                            cdnToken).ConfigureAwait(false);

                        break;
                    }
                    catch (SteamKitWebRequestException e)
                    {
                        // If the CDN returned 403, attempt to get a cdn auth if we didn't yet
                        if (e.StatusCode == HttpStatusCode.Forbidden &&
                            !steam3.CDNAuthTokens.ContainsKey((depotId, connection.Host)))
                        {
                            await steam3.RequestCDNAuthToken(appId, depotId, connection);

                            cdnPool.ReturnConnection(connection);

                            continue;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        retryCount++;
                        if (retryCount >= maxRetries)
                        {
                            Console.WriteLine("Error: {0}", e.Message);
                            throw;
                        }
                        Console.WriteLine("Retrying {0}/{1} due to error: {2}", retryCount, maxRetries, e.Message);
                    }
                }
                manifest.SaveToFile(Path.Combine(path, appId.ToString(),
                    $"{depotId.ToString()}_{manifestId.ToString()}.manifest"));
                cdnPool.ReturnConnection(connection);
                return;
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: {0}", e.Message);
            }
        }
    }
}

