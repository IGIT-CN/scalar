using Scalar.Common;
using Scalar.Common.FileSystem;
using Scalar.Common.NamedPipes;
using Scalar.Common.RepoRegistry;
using Scalar.Common.Tracing;
using Scalar.Platform.Windows;
using Scalar.Service.Handlers;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.ServiceProcess;
using System.Threading;

namespace Scalar.Service
{
    public class WindowsScalarService : ServiceBase
    {
        private const string ServiceNameArgPrefix = "--servicename=";
        private const string EtwArea = "ScalarService";

        private JsonTracer tracer;
        private Thread serviceThread;
        private ManualResetEvent serviceStopped;
        private string serviceName;
        private string serviceDataLocation;
        private string repoRegistryLocation;
        private IScalarRepoRegistry repoRegistry;
        private ProductUpgradeTimer productUpgradeTimer;
        private RequestHandler requestHandler;
        private MaintenanceTaskScheduler maintenanceTaskScheduler;
        private INotificationHandler notificationHandler;

        public WindowsScalarService(JsonTracer tracer)
        {
            this.tracer = tracer;
            this.serviceName = ScalarConstants.Service.ServiceName;
            this.CanHandleSessionChangeEvent = true;
            this.notificationHandler = new WindowsNotificationHandler(tracer);
            this.productUpgradeTimer = new ProductUpgradeTimer(tracer, this.notificationHandler);
        }

        public void Run()
        {
            try
            {
                EventMetadata metadata = new EventMetadata();
                metadata.Add("Version", ProcessHelper.GetCurrentProcessVersion());
                this.tracer.RelatedEvent(EventLevel.Informational, $"ScalarService_{nameof(this.Run)}", metadata);

                PhysicalFileSystem fileSystem = new PhysicalFileSystem();
                this.repoRegistry = new ScalarRepoRegistry(
                    this.tracer,
                    fileSystem,
                    this.repoRegistryLocation);

                this.maintenanceTaskScheduler = new MaintenanceTaskScheduler(this.tracer, fileSystem, new WindowsScalarVerbRunner(this.tracer), this.repoRegistry);

                this.AssignCurrentLoggedOnUser();
                this.maintenanceTaskScheduler.ScheduleRecurringTasks();

                this.requestHandler = new RequestHandler(this.tracer, EtwArea);

                string pipeName = ScalarPlatform.Instance.GetScalarServiceNamedPipeName(this.serviceName);
                this.tracer.RelatedInfo("Starting pipe server with name: " + pipeName);

                using (NamedPipeServer pipeServer = NamedPipeServer.StartNewServer(
                    pipeName,
                    this.tracer,
                    this.requestHandler.HandleRequest))
                {
                    this.productUpgradeTimer.Start();

                    this.serviceStopped.WaitOne();
                }
            }
            catch (Exception e)
            {
                this.LogExceptionAndExit(e, nameof(this.Run));
            }
        }

        public void StopRunning()
        {
            if (this.serviceStopped == null)
            {
                return;
            }

            try
            {
                if (this.productUpgradeTimer != null)
                {
                    this.productUpgradeTimer.Stop();
                }

                if (this.tracer != null)
                {
                    this.tracer.RelatedInfo("Stopping");
                }

                if (this.serviceStopped != null)
                {
                    this.serviceStopped.Set();
                }

                if (this.serviceThread != null)
                {
                    this.serviceThread.Join();
                    this.serviceThread = null;

                    if (this.maintenanceTaskScheduler != null)
                    {
                        this.maintenanceTaskScheduler.Dispose();
                        this.maintenanceTaskScheduler = null;
                    }

                    if (this.serviceStopped != null)
                    {
                        this.serviceStopped.Dispose();
                        this.serviceStopped = null;
                    }
                }
            }
            catch (Exception e)
            {
                this.LogExceptionAndExit(e, nameof(this.StopRunning));
            }
        }

        protected override void OnSessionChange(SessionChangeDescription changeDescription)
        {
            try
            {
                base.OnSessionChange(changeDescription);

                if (!ScalarEnlistment.IsUnattended(tracer: null))
                {
                    if (changeDescription.Reason == SessionChangeReason.SessionLogon)
                    {
                        this.tracer.RelatedInfo("SessionLogon detected, sessionId: {0}", changeDescription.SessionId);
                        this.TryAssignUserFromSessionId(changeDescription.SessionId);

                    }
                    else if (changeDescription.Reason == SessionChangeReason.SessionLogoff)
                    {
                        this.tracer.RelatedInfo($"SessionLogoff detected {changeDescription.SessionId}");
                    }
                }
            }
            catch (Exception e)
            {
                this.LogExceptionAndExit(e, nameof(this.OnSessionChange));
            }
        }

        protected override void OnStart(string[] args)
        {
            if (this.serviceThread != null)
            {
                throw new InvalidOperationException("Cannot start service twice in a row.");
            }

            // TODO: 865304 Used for functional tests and development only. Replace with a smarter appConfig-based solution
            string serviceName = args.FirstOrDefault(arg => arg.StartsWith(ServiceNameArgPrefix));
            if (serviceName != null)
            {
                this.serviceName = serviceName.Substring(ServiceNameArgPrefix.Length);
            }

            string serviceLogsDirectoryPath = ScalarPlatform.Instance.GetLogsDirectoryForGVFSComponent(this.serviceName);

            // Create the logs directory explicitly *before* creating a log file event listener to ensure that it
            // and its ancestor directories are created with the correct ACLs.
            this.CreateServiceLogsDirectory(serviceLogsDirectoryPath);
            this.tracer.AddLogFileEventListener(
                ScalarEnlistment.GetNewScalarLogFileName(serviceLogsDirectoryPath, ScalarConstants.LogFileTypes.Service),
                EventLevel.Verbose,
                Keywords.Any);

            try
            {
                this.serviceDataLocation = ScalarPlatform.Instance.GetSecureDataRootForScalarComponent(this.serviceName);
                this.repoRegistryLocation = ScalarPlatform.Instance.GetCommonAppDataRootForScalarComponent(ScalarConstants.RepoRegistry.RegistryDirectoryName);
                this.CreateAndConfigureProgramDataDirectories();
                this.Start();
            }
            catch (Exception e)
            {
                this.LogExceptionAndExit(e, nameof(this.OnStart));
            }
        }

        protected override void OnStop()
        {
            try
            {
                this.StopRunning();
            }
            catch (Exception e)
            {
                this.LogExceptionAndExit(e, nameof(this.OnStart));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.StopRunning();

                if (this.tracer != null)
                {
                    this.tracer.Dispose();
                    this.tracer = null;
                }
            }

            base.Dispose(disposing);
        }

        private void Start()
        {
            if (this.serviceStopped != null)
            {
                return;
            }

            this.serviceStopped = new ManualResetEvent(false);
            this.serviceThread = new Thread(this.Run);

            this.serviceThread.Start();
        }

        private void AssignCurrentLoggedOnUser()
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = "powershell";
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.Arguments = "Get-Process"
                                 + " | Select-Object -Unique -ExpandProperty SessionId";

            Process process = Process.Start(startInfo);
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            string[] ids = output.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < ids.Length; i++)
            {
                if (int.TryParse(ids[i], out int sessionId) &&
                    sessionId != 0 &&
                    this.TryAssignUserFromSessionId(sessionId))
                {
                    // This one worked!
                    break;
                }
            }
        }

        private bool TryAssignUserFromSessionId(int sessionId)
        {
            this.LaunchServiceUIIfNotRunning(sessionId);

            string userId = ScalarPlatform.Instance.GetUserIdFromLoginSessionId(sessionId, this.tracer);

            if (string.IsNullOrEmpty(userId))
            {
                this.tracer.RelatedInfo($"Failed to use sessionId {sessionId}");
                return false;
            }

            this.maintenanceTaskScheduler.RegisterUser(
                new UserAndSession(userId, sessionId));

            this.tracer.RelatedInfo($"Succeeded with sessionId {sessionId}");
            return true;
        }

        private void LogExceptionAndExit(Exception e, string method)
        {
            EventMetadata metadata = new EventMetadata();
            metadata.Add("Area", EtwArea);
            metadata.Add("Exception", e.ToString());
            this.tracer.RelatedError(metadata, "Unhandled exception in " + method);
            Environment.Exit((int)ReturnCode.GenericError);
        }

        private void CreateServiceLogsDirectory(string serviceLogsDirectoryPath)
        {
            if (!Directory.Exists(serviceLogsDirectoryPath))
            {
                Directory.CreateDirectory(serviceLogsDirectoryPath);
            }
        }

        private void CreateAndConfigureProgramDataDirectories()
        {
            string serviceDataRootPath = ScalarPlatform.Instance.GetSecureDataRootForScalar();

            // Create Scalar.Service and Scalar.Upgrade related directories (if they don't already exist)
            // TODO #136: Determine if we still should be creating Scalar.Service here
            DirectoryEx.CreateDirectory(serviceDataRootPath);
            DirectoryEx.CreateDirectory(this.serviceDataLocation);
            DirectoryEx.CreateDirectory(ProductUpgraderInfo.GetUpgradeProtectedDataDirectory());

            // Special rules for the upgrader logs and registry, as non-elevated users need to be be able to write
            this.CreateAndConfigureUserWriteableDirectory(this.repoRegistryLocation);
            this.CreateAndConfigureUserWriteableDirectory(ProductUpgraderInfo.GetLogDirectoryPath());
            this.CreateAndConfigureUserWriteableDirectory(ScalarPlatform.Instance.GetLogsDirectoryForGVFSComponent(ScalarConstants.Service.UIName));
        }

        private void CreateAndConfigureUserWriteableDirectory(string path)
        {
            string upgradeLogsPath = ProductUpgraderInfo.GetLogDirectoryPath();

            string error;
            if (!ScalarPlatform.Instance.FileSystem.TryCreateDirectoryWithAdminAndUserModifyPermissions(path, out error))
            {
                EventMetadata metadata = new EventMetadata();
                metadata.Add("Area", EtwArea);
                metadata.Add(nameof(path), path);
                metadata.Add(nameof(error), error);
                this.tracer.RelatedWarning(
                    metadata,
                    $"{nameof(this.CreateAndConfigureUserWriteableDirectory)}: Failed to create upgrade logs directory",
                    Keywords.Telemetry);
            }
        }

        private void LaunchServiceUIIfNotRunning(int sessionId)
        {
            NamedPipeClient client;
            using (client = new NamedPipeClient(ScalarConstants.Service.UIName))
            {
                if (!client.Connect())
                {
                    this.tracer.RelatedError($"Could not connect with {ScalarConstants.Service.UIName}. Attempting to relaunch.");

                    this.TerminateExistingProcess(ScalarConstants.Service.UIName, sessionId);

                    CurrentUser currentUser = new CurrentUser(this.tracer, sessionId);
                    if (!currentUser.RunAs(
                        Configuration.Instance.ScalarServiceUILocation,
                        string.Empty))
                    {
                        this.tracer.RelatedError("Could not start " + ScalarConstants.Service.UIName);
                    }
                    else
                    {
                        this.tracer.RelatedInfo($"Successfully launched {ScalarConstants.Service.UIName}. ");
                    }
                }
            }
        }

        private void TerminateExistingProcess(string processName, int sessionId)
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName(processName))
                {
                    if (process.SessionId == sessionId)
                    {
                        this.tracer.RelatedInfo($"{nameof(this.TerminateExistingProcess)}- Stopping {processName}, in session {sessionId}.");

                        process.Kill();
                    }
                }
            }
            catch (Exception ex)
            {
                this.tracer.RelatedError("Could not find and kill existing instances of {0}: {1}", processName, ex.Message);
            }
        }
    }
}
