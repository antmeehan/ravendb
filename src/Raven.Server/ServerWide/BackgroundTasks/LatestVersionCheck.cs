﻿using System;
using System.Net.Http;
using System.Threading;
using Raven.Client.Util;
using Sparrow.Logging;
using Raven.Server.Json;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.NotificationCenter.Notifications.Details;
using Sparrow.Collections;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Server.ServerWide.BackgroundTasks
{
    public static class LatestVersionCheck
    {
        private const string ApiRavenDbNet = "https://api.ravendb.net";

        private static readonly Logger _logger = LoggingSource.Instance.GetLogger("global", typeof(LatestVersionCheck).FullName);

        private static AlertRaised _alert;

        private static Timer _timer;

        private static readonly ConcurrentSet<WeakReference<ServerStore>> ServerStores = new ConcurrentSet<WeakReference<ServerStore>>();

        private static readonly HttpClient ApiRavenDbClient = new HttpClient
        {
            BaseAddress = new Uri(ApiRavenDbNet)
        };

        static LatestVersionCheck()
        {
            _timer = new Timer(state => PerformAsync(), null, (int)TimeSpan.FromMinutes(5).TotalMilliseconds, (int)TimeSpan.FromHours(12).TotalMilliseconds);
        }

        public static void Check(ServerStore serverStore)
        {
            ServerStores.Add(new WeakReference<ServerStore>(serverStore));

            var alert = _alert;
            if (alert == null)
                return;

            serverStore.NotificationCenter.Add(_alert);
        }

        private static async void PerformAsync()
        {
            try
            {
                // TODO @gregolsky make channel customizable 
                var stream =
                    await ApiRavenDbClient.GetStreamAsync("/api/v1/versions/latest?channel=dev&min=40000&max=49999");

                using (var context = JsonOperationContext.ShortTermSingleUse())
                {
                    var json = context.ReadForMemory(stream, "latest/version");
                    var latestVersionInfo = JsonDeserializationServer.LatestVersionCheckVersionInfo(json);

                    if (ServerVersion.Build != ServerVersion.DevBuildNumber &&
                        latestVersionInfo?.BuildNumber > ServerVersion.Build)
                    {
                        var severityInfo = DetermineSeverity(latestVersionInfo);

                        _alert = AlertRaised.Create("RavenDB update available", $"Version {latestVersionInfo.Version} is available",
                            AlertType.Server_NewVersionAvailable, severityInfo,
                            details: new NewVersionAvailableDetails(latestVersionInfo));

                        AddAlertToNotificationCenter();
                    }
                }
            }
            catch (Exception err)
            {
                if (_logger.IsInfoEnabled)
                    _logger.Info("Error getting latest version info.", err);
            }
        }

        private static void AddAlertToNotificationCenter()
        {
            foreach (var weak in ServerStores)
            {

                if (weak.TryGetTarget(out ServerStore serverStore) == false || serverStore == null || serverStore.Disposed)
                {
                    ServerStores.TryRemove(weak);
                    continue;
                }

                try
                {
                    serverStore.NotificationCenter.Add(_alert);
                }
                catch (Exception err)
                {
                    if (_logger.IsInfoEnabled)
                        _logger.Info("Error adding latest version alert to notification center.", err);
                }
            }
        }

        private static NotificationSeverity DetermineSeverity(VersionInfo latestVersionInfo)
        {
            var diff = SystemTime.UtcNow - latestVersionInfo.PublishedAt;
            var severityInfo = NotificationSeverity.Info;
            if (diff.TotalDays > 21)
            {
                severityInfo = NotificationSeverity.Error;
            }
            else if (diff.TotalDays > 7)
            {
                severityInfo = NotificationSeverity.Warning;
            }
            return severityInfo;
        }

        public class VersionInfo
        {
            public string Version { get; set; }

            public int BuildNumber { get; set; }

            public string BuildType { get; set; }

            public DateTime PublishedAt { get; set; }

            public DynamicJsonValue ToJson()
            {
                return new DynamicJsonValue(GetType())
                {
                    [nameof(Version)] = Version,
                    [nameof(BuildNumber)] = BuildNumber,
                    [nameof(BuildType)] = BuildType,
                    [nameof(PublishedAt)] = PublishedAt
                };
            }
        }
    }
}
