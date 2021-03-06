// Copyright (c) Microsoft. All rights reserved.
namespace NetworkController
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.ModuleUtil;
    using Microsoft.Azure.Devices.Edge.ModuleUtil.NetworkController;
    using Microsoft.Azure.Devices.Edge.ModuleUtil.TestResults;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Extensions.Logging;

    class Program
    {
        static readonly ILogger Log = Logger.Factory.CreateLogger<Program>();

        static async Task Main()
        {
            (CancellationTokenSource cts, ManualResetEventSlim completed, Option<object> handler) = ShutdownHandler.Init(TimeSpan.FromSeconds(5), Log);

            Log.LogInformation($"Starting with {Settings.Current.NetworkRunProfile.ProfileType} Settings: {Settings.Current.NetworkRunProfile.ProfileSetting}");

            try
            {
                var networkInterfaceName = DockerHelper.GetDockerInterfaceName();

                if (networkInterfaceName.HasValue)
                {
                    await networkInterfaceName.ForEachAsync(async name =>
                    {
                        var offline = new OfflineController(name, Settings.Current.IotHubHostname, Settings.Current.NetworkRunProfile.ProfileSetting);
                        var satellite = new SatelliteController(name, Settings.Current.IotHubHostname, Settings.Current.NetworkRunProfile.ProfileSetting);
                        var cellular = new CellularController(name, Settings.Current.IotHubHostname, Settings.Current.NetworkRunProfile.ProfileSetting);
                        var controllers = new List<INetworkController>() { offline, satellite, cellular };
                        await RemoveAllControllingRules(controllers, cts.Token);

                        switch (Settings.Current.NetworkRunProfile.ProfileType)
                        {
                            case NetworkControllerType.Offline:
                                await StartAsync(offline, cts.Token);
                                break;
                            case NetworkControllerType.Satellite:
                                await StartAsync(satellite, cts.Token);
                                break;
                            case NetworkControllerType.Cellular:
                                await StartAsync(cellular, cts.Token);
                                break;
                            case NetworkControllerType.Online:
                                Log.LogInformation($"No restrictions to be set, running as online");
                                break;
                            default:
                                throw new NotSupportedException($"Network type {Settings.Current.NetworkRunProfile.ProfileType} is not supported.");
                        }
                    });
                }
                else
                {
                    Log.LogError($"No network interface found for docker network {Settings.Current.NetworkId}");
                }
            }
            catch (Exception ex)
            {
                Log.LogError(ex, $"Unexpected exception thrown from {nameof(Main)} method");
            }

            await cts.Token.WhenCanceled();
            completed.Set();
            handler.ForEach(h => GC.KeepAlive(h));
        }

        static async Task ReportTestInfoAsync()
        {
            var testInfoResults = new string[]
            {
                $"Network Run Profile={Settings.Current.NetworkRunProfile}",
                $"Network Network Id={Settings.Current.NetworkId}",
                $"Network Frequencies={string.Join(",", Settings.Current.Frequencies.Select(f => $"[offline:{f.OfflineFrequency},Online:{f.OnlineFrequency},Runs:{f.RunsCount}]"))}"
            };

            var testResultReportingClient = new TestResultReportingClient() { BaseUrl = Settings.Current.TestResultCoordinatorEndpoint.AbsoluteUri };

            foreach (string testInfo in testInfoResults)
            {
                await ModuleUtil.ReportTestResultAsync(
                    testResultReportingClient,
                    Log,
                    new TestInfoResult(
                        Settings.Current.TrackingId,
                        Settings.Current.ModuleId,
                        testInfo,
                        DateTime.UtcNow));
            }
        }

        static async Task StartAsync(INetworkController controller, CancellationToken cancellationToken)
        {
            TimeSpan delay = Settings.Current.StartAfter;

            INetworkStatusReporter reporter = new NetworkStatusReporter(Settings.Current.TestResultCoordinatorEndpoint, Settings.Current.ModuleId, Settings.Current.TrackingId);
            foreach (Frequency item in Settings.Current.Frequencies)
            {
                Log.LogInformation($"Schedule task for type {controller.NetworkControllerType} to start after {delay} Offline frequency {item.OfflineFrequency} Online frequency {item.OnlineFrequency} Run times {item.RunsCount}");

                await Task.Delay(delay, cancellationToken);
                await ReportTestInfoAsync();

                var taskExecutor = new CountedTaskExecutor(
                    async cs =>
                    {
                        await SetNetworkControllerStatus(controller, NetworkControllerStatus.Enabled, reporter, cs);
                        await Task.Delay(item.OfflineFrequency, cs);
                        await SetNetworkControllerStatus(controller, NetworkControllerStatus.Disabled, reporter, cs);
                    },
                    TimeSpan.Zero,
                    item.OnlineFrequency,
                    item.RunsCount,
                    Log,
                    "restrict/default");

                await taskExecutor.Schedule(cancellationToken);

                // Only needs to set the start delay for first frequency, after that reset to 0
                delay = TimeSpan.FromSeconds(0);
            }
        }

        static async Task SetNetworkControllerStatus(INetworkController controller, NetworkControllerStatus networkControllerStatus, INetworkStatusReporter reporter, CancellationToken cs)
        {
            await reporter.ReportNetworkStatusAsync(NetworkControllerOperation.SettingRule, networkControllerStatus, controller.NetworkControllerType);
            bool success = await controller.SetNetworkControllerStatusAsync(networkControllerStatus, cs);
            success = await CheckSetNetworkControllerStatusAsyncResult(success, networkControllerStatus, controller, cs);
            await reporter.ReportNetworkStatusAsync(NetworkControllerOperation.RuleSet, networkControllerStatus, controller.NetworkControllerType, success);
        }

        static async Task RemoveAllControllingRules(IList<INetworkController> controllerList, CancellationToken cancellationToken)
        {
            var reporter = new NetworkStatusReporter(Settings.Current.TestResultCoordinatorEndpoint, Settings.Current.ModuleId, Settings.Current.TrackingId);
            await reporter.ReportNetworkStatusAsync(NetworkControllerOperation.SettingRule, NetworkControllerStatus.Disabled, NetworkControllerType.All);

            foreach (var controller in controllerList)
            {
                NetworkControllerStatus networkControllerStatus = await controller.GetNetworkControllerStatusAsync(cancellationToken);
                if (networkControllerStatus != NetworkControllerStatus.Disabled)
                {
                    Log.LogInformation($"Network restriction is enabled for {controller.NetworkControllerType}. Setting default");
                    bool online = await controller.SetNetworkControllerStatusAsync(NetworkControllerStatus.Disabled, cancellationToken);
                    online = await CheckSetNetworkControllerStatusAsyncResult(online, NetworkControllerStatus.Disabled, controller, cancellationToken);
                    if (!online)
                    {
                        Log.LogError($"Failed to ensure it starts with default values.");
                        await reporter.ReportNetworkStatusAsync(NetworkControllerOperation.RuleSet, NetworkControllerStatus.Enabled, controller.NetworkControllerType, true);
                        throw new TestInitializationException();
                    }
                }
            }

            Log.LogInformation($"Network is online");
            await reporter.ReportNetworkStatusAsync(NetworkControllerOperation.RuleSet, NetworkControllerStatus.Disabled, NetworkControllerType.All, true);
        }

        static async Task<bool> CheckSetNetworkControllerStatusAsyncResult(bool success, NetworkControllerStatus networkControllerStatus, INetworkController controller, CancellationToken cs)
        {
            NetworkControllerStatus reportedStatus = await controller.GetNetworkControllerStatusAsync(cs);

            string resultMessage = success ? "succeded" : "failed";
            Log.LogInformation($"Command SetNetworkControllerStatus to {networkControllerStatus} execution {resultMessage}, network status {reportedStatus}");

            return success && reportedStatus == networkControllerStatus;
        }
    }
}
