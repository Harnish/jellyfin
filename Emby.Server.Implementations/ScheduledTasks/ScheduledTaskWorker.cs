#nullable disable

#pragma warning disable CS1591

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Progress;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks
{
    /// <summary>
    /// Class ScheduledTaskWorker.
    /// </summary>
    public class ScheduledTaskWorker : IScheduledTaskWorker
    {
        /// <summary>
        /// The options for the json Serializer.
        /// </summary>
        private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;

        /// <summary>
        /// Gets or sets the application paths.
        /// </summary>
        /// <value>The application paths.</value>
        private readonly IApplicationPaths _applicationPaths;

        /// <summary>
        /// Gets or sets the logger.
        /// </summary>
        /// <value>The logger.</value>
        private readonly ILogger _logger;

        /// <summary>
        /// Gets or sets the task manager.
        /// </summary>
        /// <value>The task manager.</value>
        private readonly ITaskManager _taskManager;

        /// <summary>
        /// The _last execution result sync lock.
        /// </summary>
        private readonly object _lastExecutionResultSyncLock = new object();

        private bool _readFromFile = false;

        /// <summary>
        /// The _last execution result.
        /// </summary>
        private TaskResult _lastExecutionResult;

        private Task _currentTask;

        /// <summary>
        /// The _triggers.
        /// </summary>
        private Tuple<TaskTriggerInfo, ITaskTrigger>[] _triggers;

        /// <summary>
        /// The _id.
        /// </summary>
        private string _id;

        /// <summary>
        /// Initializes a new instance of the <see cref="ScheduledTaskWorker" /> class.
        /// </summary>
        /// <param name="scheduledTask">The scheduled task.</param>
        /// <param name="applicationPaths">The application paths.</param>
        /// <param name="taskManager">The task manager.</param>
        /// <param name="logger">The logger.</param>
        /// <exception cref="ArgumentNullException">
        /// scheduledTask
        /// or
        /// applicationPaths
        /// or
        /// taskManager
        /// or
        /// jsonSerializer
        /// or
        /// logger.
        /// </exception>
        public ScheduledTaskWorker(IScheduledTask scheduledTask, IApplicationPaths applicationPaths, ITaskManager taskManager, ILogger logger)
        {
            if (scheduledTask == null)
            {
                throw new ArgumentNullException(nameof(scheduledTask));
            }

            if (applicationPaths == null)
            {
                throw new ArgumentNullException(nameof(applicationPaths));
            }

            if (taskManager == null)
            {
                throw new ArgumentNullException(nameof(taskManager));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            ScheduledTask = scheduledTask;
            _applicationPaths = applicationPaths;
            _taskManager = taskManager;
            _logger = logger;

            InitTriggerEvents();
        }

        public event EventHandler<GenericEventArgs<double>> TaskProgress;

        /// <summary>
        /// Gets the scheduled task.
        /// </summary>
        /// <value>The scheduled task.</value>
        public IScheduledTask ScheduledTask { get; private set; }

        /// <summary>
        /// Gets the last execution result.
        /// </summary>
        /// <value>The last execution result.</value>
        public TaskResult LastExecutionResult
        {
            get
            {
                var path = GetHistoryFilePath();

                lock (_lastExecutionResultSyncLock)
                {
                    if (_lastExecutionResult == null && !_readFromFile)
                    {
                        if (File.Exists(path))
                        {
                            var bytes = File.ReadAllBytes(path);
                            if (bytes.Length > 0)
                            {
                                try
                                {
                                    _lastExecutionResult = JsonSerializer.Deserialize<TaskResult>(bytes, _jsonOptions);
                                }
                                catch (JsonException ex)
                                {
                                    _logger.LogError(ex, "Error deserializing {File}", path);
                                }
                            }
                            else
                            {
                                _logger.LogDebug("Scheduled Task history file {Path} is empty. Skipping deserialization.", path);
                            }
                        }

                        _readFromFile = true;
                    }
                }

                return _lastExecutionResult;
            }

            private set
            {
                _lastExecutionResult = value;

                var path = GetHistoryFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                lock (_lastExecutionResultSyncLock)
                {
                    using FileStream createStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                    using Utf8JsonWriter jsonStream = new Utf8JsonWriter(createStream);
                    JsonSerializer.Serialize(jsonStream, value, _jsonOptions);
                }
            }
        }

        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <value>The name.</value>
        public string Name => ScheduledTask.Name;

        /// <summary>
        /// Gets the description.
        /// </summary>
        /// <value>The description.</value>
        public string Description => ScheduledTask.Description;

        /// <summary>
        /// Gets the category.
        /// </summary>
        /// <value>The category.</value>
        public string Category => ScheduledTask.Category;

        /// <summary>
        /// Gets or sets the current cancellation token.
        /// </summary>
        /// <value>The current cancellation token source.</value>
        private CancellationTokenSource CurrentCancellationTokenSource { get; set; }

        /// <summary>
        /// Gets or sets the current execution start time.
        /// </summary>
        /// <value>The current execution start time.</value>
        private DateTime CurrentExecutionStartTime { get; set; }

        /// <summary>
        /// Gets the state.
        /// </summary>
        /// <value>The state.</value>
        public TaskState State
        {
            get
            {
                if (CurrentCancellationTokenSource != null)
                {
                    return CurrentCancellationTokenSource.IsCancellationRequested
                               ? TaskState.Cancelling
                               : TaskState.Running;
                }

                return TaskState.Idle;
            }
        }

        /// <summary>
        /// Gets the current progress.
        /// </summary>
        /// <value>The current progress.</value>
        public double? CurrentProgress { get; private set; }

        /// <summary>
        /// Gets or sets the triggers that define when the task will run.
        /// </summary>
        /// <value>The triggers.</value>
        private Tuple<TaskTriggerInfo, ITaskTrigger>[] InternalTriggers
        {
            get => _triggers;
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }

                // Cleanup current triggers
                if (_triggers != null)
                {
                    DisposeTriggers();
                }

                _triggers = value.ToArray();

                ReloadTriggerEvents(false);
            }
        }

        /// <summary>
        /// Gets or sets the triggers that define when the task will run.
        /// </summary>
        /// <value>The triggers.</value>
        /// <exception cref="ArgumentNullException"><c>value</c> is <c>null</c>.</exception>
        public TaskTriggerInfo[] Triggers
        {
            get
            {
                var triggers = InternalTriggers;
                return triggers.Select(i => i.Item1).ToArray();
            }

            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }

                // This null check is not great, but is needed to handle bad user input, or user mucking with the config file incorrectly
                var triggerList = value.Where(i => i != null).ToArray();

                SaveTriggers(triggerList);

                InternalTriggers = triggerList.Select(i => new Tuple<TaskTriggerInfo, ITaskTrigger>(i, GetTrigger(i))).ToArray();
            }
        }

        /// <summary>
        /// Gets the unique id.
        /// </summary>
        /// <value>The unique id.</value>
        public string Id
        {
            get
            {
                return _id ??= ScheduledTask.GetType().FullName.GetMD5().ToString("N", CultureInfo.InvariantCulture);
            }
        }

        private void InitTriggerEvents()
        {
            _triggers = LoadTriggers();
            ReloadTriggerEvents(true);
        }

        public void ReloadTriggerEvents()
        {
            ReloadTriggerEvents(false);
        }

        /// <summary>
        /// Reloads the trigger events.
        /// </summary>
        /// <param name="isApplicationStartup">if set to <c>true</c> [is application startup].</param>
        private void ReloadTriggerEvents(bool isApplicationStartup)
        {
            foreach (var triggerInfo in InternalTriggers)
            {
                var trigger = triggerInfo.Item2;

                trigger.Stop();

                trigger.Triggered -= OnTriggerTriggered;
                trigger.Triggered += OnTriggerTriggered;
                trigger.Start(LastExecutionResult, _logger, Name, isApplicationStartup);
            }
        }

        /// <summary>
        /// Handles the Triggered event of the trigger control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs" /> instance containing the event data.</param>
        private async void OnTriggerTriggered(object sender, EventArgs e)
        {
            var trigger = (ITaskTrigger)sender;

            if (ScheduledTask is IConfigurableScheduledTask configurableTask && !configurableTask.IsEnabled)
            {
                return;
            }

            _logger.LogInformation("{0} fired for task: {1}", trigger.GetType().Name, Name);

            trigger.Stop();

            _taskManager.QueueScheduledTask(ScheduledTask, trigger.TaskOptions);

            await Task.Delay(1000).ConfigureAwait(false);

            trigger.Start(LastExecutionResult, _logger, Name, false);
        }

        /// <summary>
        /// Executes the task.
        /// </summary>
        /// <param name="options">Task options.</param>
        /// <returns>Task.</returns>
        /// <exception cref="InvalidOperationException">Cannot execute a Task that is already running.</exception>
        public async Task Execute(TaskOptions options)
        {
            var task = Task.Run(async () => await ExecuteInternal(options).ConfigureAwait(false));

            _currentTask = task;

            try
            {
                await task.ConfigureAwait(false);
            }
            finally
            {
                _currentTask = null;
                GC.Collect();
            }
        }

        private async Task ExecuteInternal(TaskOptions options)
        {
            // Cancel the current execution, if any
            if (CurrentCancellationTokenSource != null)
            {
                throw new InvalidOperationException("Cannot execute a Task that is already running");
            }

            var progress = new SimpleProgress<double>();

            CurrentCancellationTokenSource = new CancellationTokenSource();

            _logger.LogInformation("Executing {0}", Name);

            ((TaskManager)_taskManager).OnTaskExecuting(this);

            progress.ProgressChanged += OnProgressChanged;

            TaskCompletionStatus status;
            CurrentExecutionStartTime = DateTime.UtcNow;

            Exception failureException = null;

            try
            {
                if (options != null && options.MaxRuntimeTicks.HasValue)
                {
                    CurrentCancellationTokenSource.CancelAfter(TimeSpan.FromTicks(options.MaxRuntimeTicks.Value));
                }

                await ScheduledTask.Execute(CurrentCancellationTokenSource.Token, progress).ConfigureAwait(false);

                status = TaskCompletionStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                status = TaskCompletionStatus.Cancelled;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error");

                failureException = ex;

                status = TaskCompletionStatus.Failed;
            }

            var startTime = CurrentExecutionStartTime;
            var endTime = DateTime.UtcNow;

            progress.ProgressChanged -= OnProgressChanged;
            CurrentCancellationTokenSource.Dispose();
            CurrentCancellationTokenSource = null;
            CurrentProgress = null;

            OnTaskCompleted(startTime, endTime, status, failureException);
        }

        /// <summary>
        /// Progress_s the progress changed.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The e.</param>
        private void OnProgressChanged(object sender, double e)
        {
            e = Math.Min(e, 100);

            CurrentProgress = e;

            TaskProgress?.Invoke(this, new GenericEventArgs<double>(e));
        }

        /// <summary>
        /// Stops the task if it is currently executing.
        /// </summary>
        /// <exception cref="InvalidOperationException">Cannot cancel a Task unless it is in the Running state.</exception>
        public void Cancel()
        {
            if (State != TaskState.Running)
            {
                throw new InvalidOperationException("Cannot cancel a Task unless it is in the Running state.");
            }

            CancelIfRunning();
        }

        /// <summary>
        /// Cancels if running.
        /// </summary>
        public void CancelIfRunning()
        {
            if (State == TaskState.Running)
            {
                _logger.LogInformation("Attempting to cancel Scheduled Task {0}", Name);
                CurrentCancellationTokenSource.Cancel();
            }
        }

        /// <summary>
        /// Gets the scheduled tasks configuration directory.
        /// </summary>
        /// <returns>System.String.</returns>
        private string GetScheduledTasksConfigurationDirectory()
        {
            return Path.Combine(_applicationPaths.ConfigurationDirectoryPath, "ScheduledTasks");
        }

        /// <summary>
        /// Gets the scheduled tasks data directory.
        /// </summary>
        /// <returns>System.String.</returns>
        private string GetScheduledTasksDataDirectory()
        {
            return Path.Combine(_applicationPaths.DataPath, "ScheduledTasks");
        }

        /// <summary>
        /// Gets the history file path.
        /// </summary>
        /// <value>The history file path.</value>
        private string GetHistoryFilePath()
        {
            return Path.Combine(GetScheduledTasksDataDirectory(), new Guid(Id) + ".js");
        }

        /// <summary>
        /// Gets the configuration file path.
        /// </summary>
        /// <returns>System.String.</returns>
        private string GetConfigurationFilePath()
        {
            return Path.Combine(GetScheduledTasksConfigurationDirectory(), new Guid(Id) + ".js");
        }

        /// <summary>
        /// Loads the triggers.
        /// </summary>
        /// <returns>IEnumerable{BaseTaskTrigger}.</returns>
        private Tuple<TaskTriggerInfo, ITaskTrigger>[] LoadTriggers()
        {
            // This null check is not great, but is needed to handle bad user input, or user mucking with the config file incorrectly
            var settings = LoadTriggerSettings().Where(i => i != null).ToArray();

            return settings.Select(i => new Tuple<TaskTriggerInfo, ITaskTrigger>(i, GetTrigger(i))).ToArray();
        }

        private TaskTriggerInfo[] LoadTriggerSettings()
        {
            string path = GetConfigurationFilePath();
            TaskTriggerInfo[] list = null;
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                list = JsonSerializer.Deserialize<TaskTriggerInfo[]>(bytes, _jsonOptions);
            }

            // Return defaults if file doesn't exist.
            return list ?? GetDefaultTriggers();
        }

        private TaskTriggerInfo[] GetDefaultTriggers()
        {
            try
            {
                return ScheduledTask.GetDefaultTriggers().ToArray();
            }
            catch
            {
                return new TaskTriggerInfo[]
                {
                    new TaskTriggerInfo
                    {
                        IntervalTicks = TimeSpan.FromDays(1).Ticks,
                        Type = TaskTriggerInfo.TriggerInterval
                    }
                };
            }
        }

        /// <summary>
        /// Saves the triggers.
        /// </summary>
        /// <param name="triggers">The triggers.</param>
        private void SaveTriggers(TaskTriggerInfo[] triggers)
        {
            var path = GetConfigurationFilePath();

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using FileStream createStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using Utf8JsonWriter jsonWriter = new Utf8JsonWriter(createStream);
            JsonSerializer.Serialize(jsonWriter, triggers, _jsonOptions);
        }

        /// <summary>
        /// Called when [task completed].
        /// </summary>
        /// <param name="startTime">The start time.</param>
        /// <param name="endTime">The end time.</param>
        /// <param name="status">The status.</param>
        /// <param name="ex">The exception.</param>
        private void OnTaskCompleted(DateTime startTime, DateTime endTime, TaskCompletionStatus status, Exception ex)
        {
            var elapsedTime = endTime - startTime;

            _logger.LogInformation("{0} {1} after {2} minute(s) and {3} seconds", Name, status, Math.Truncate(elapsedTime.TotalMinutes), elapsedTime.Seconds);

            var result = new TaskResult
            {
                StartTimeUtc = startTime,
                EndTimeUtc = endTime,
                Status = status,
                Name = Name,
                Id = Id
            };

            result.Key = ScheduledTask.Key;

            if (ex != null)
            {
                result.ErrorMessage = ex.Message;
                result.LongErrorMessage = ex.StackTrace;
            }

            LastExecutionResult = result;

            ((TaskManager)_taskManager).OnTaskCompleted(this, result);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {
                DisposeTriggers();

                var wassRunning = State == TaskState.Running;
                var startTime = CurrentExecutionStartTime;

                var token = CurrentCancellationTokenSource;
                if (token != null)
                {
                    try
                    {
                        _logger.LogInformation("{Name}: Cancelling", Name);
                        token.Cancel();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error calling CancellationToken.Cancel();");
                    }
                }

                var task = _currentTask;
                if (task != null)
                {
                    try
                    {
                        _logger.LogInformation("{Name}: Waiting on Task", Name);
                        var exited = task.Wait(2000);

                        if (exited)
                        {
                            _logger.LogInformation("{Name}: Task exited", Name);
                        }
                        else
                        {
                            _logger.LogInformation("{Name}: Timed out waiting for task to stop", Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error calling Task.WaitAll();");
                    }
                }

                if (token != null)
                {
                    try
                    {
                        _logger.LogDebug("{Name}: Disposing CancellationToken", Name);
                        token.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error calling CancellationToken.Dispose();");
                    }
                }

                if (wassRunning)
                {
                    OnTaskCompleted(startTime, DateTime.UtcNow, TaskCompletionStatus.Aborted, null);
                }
            }
        }

        /// <summary>
        /// Converts a TaskTriggerInfo into a concrete BaseTaskTrigger.
        /// </summary>
        /// <param name="info">The info.</param>
        /// <returns>BaseTaskTrigger.</returns>
        /// <exception cref="ArgumentException">Invalid trigger type:  + info.Type.</exception>
        private ITaskTrigger GetTrigger(TaskTriggerInfo info)
        {
            var options = new TaskOptions
            {
                MaxRuntimeTicks = info.MaxRuntimeTicks
            };

            if (info.Type.Equals(nameof(DailyTrigger), StringComparison.OrdinalIgnoreCase))
            {
                if (!info.TimeOfDayTicks.HasValue)
                {
                    throw new ArgumentException("Info did not contain a TimeOfDayTicks.", nameof(info));
                }

                return new DailyTrigger(TimeSpan.FromTicks(info.TimeOfDayTicks.Value), options);
            }

            if (info.Type.Equals(nameof(WeeklyTrigger), StringComparison.OrdinalIgnoreCase))
            {
                if (!info.TimeOfDayTicks.HasValue)
                {
                    throw new ArgumentException("Info did not contain a TimeOfDayTicks.", nameof(info));
                }

                if (!info.DayOfWeek.HasValue)
                {
                    throw new ArgumentException("Info did not contain a DayOfWeek.", nameof(info));
                }

                return new WeeklyTrigger(TimeSpan.FromTicks(info.TimeOfDayTicks.Value), info.DayOfWeek.Value, options);
            }

            if (info.Type.Equals(nameof(IntervalTrigger), StringComparison.OrdinalIgnoreCase))
            {
                if (!info.IntervalTicks.HasValue)
                {
                    throw new ArgumentException("Info did not contain a IntervalTicks.", nameof(info));
                }

                return new IntervalTrigger(TimeSpan.FromTicks(info.IntervalTicks.Value), options);
            }

            if (info.Type.Equals(nameof(StartupTrigger), StringComparison.OrdinalIgnoreCase))
            {
                return new StartupTrigger(options);
            }

            throw new ArgumentException("Unrecognized trigger type: " + info.Type);
        }

        /// <summary>
        /// Disposes each trigger.
        /// </summary>
        private void DisposeTriggers()
        {
            foreach (var triggerInfo in InternalTriggers)
            {
                var trigger = triggerInfo.Item2;
                trigger.Triggered -= OnTriggerTriggered;
                trigger.Stop();
            }
        }
    }
}
