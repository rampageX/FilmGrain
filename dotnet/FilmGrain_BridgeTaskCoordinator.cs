using System;
using System.Collections.Generic;

namespace FilmGrainStudioPreview
{
    internal enum BridgeTaskState
    {
        Idle,
        Running,
        Cancelling
    }

    internal sealed class BridgeTaskRequest
    {
        public BridgeExecutionRequest ExecutionRequest { get; set; }
        public bool NoReencode { get; set; }
        public bool NativeBackend { get; set; }
        public int QueueIndex { get; set; }
        public int QueueTotal { get; set; }
        public double RecommendedOutputFps { get; set; }
    }

    internal sealed class BridgeTaskStateChangedEventArgs : EventArgs
    {
        public BridgeTaskState State { get; private set; }
        public BridgeTaskRequest Task { get; private set; }

        public BridgeTaskStateChangedEventArgs(BridgeTaskState state, BridgeTaskRequest task)
        {
            State = state;
            Task = task;
        }
    }

    internal sealed class BridgeTaskItemCompletedEventArgs : EventArgs
    {
        public BridgeTaskRequest Task { get; private set; }
        public BridgeExecutionResult Result { get; private set; }
        public BridgeTaskItemCompletedEventArgs(BridgeTaskRequest task,BridgeExecutionResult result){Task=task;Result=result;}
    }

    // Phase 3.5.4 task-lifecycle coordinator.
    // Owns the validated BridgeExecutionCore and keeps execution state out of MainForm.
    // Process launching, progress parsing, cancellation and cleanup remain in BridgeExecutionCore.
    internal sealed class BridgeTaskCoordinator : IDisposable
    {
        private readonly object sync = new object();
        private BridgeExecutionCore executionCore;
        private BridgeTaskState state = BridgeTaskState.Idle;
        private BridgeTaskRequest activeTask;
        private bool disposed;
        private Queue<BridgeTaskRequest> pending = new Queue<BridgeTaskRequest>();

        public event EventHandler<BridgeTaskStateChangedEventArgs> StateChanged;
        public event EventHandler<BridgeLogLineEventArgs> LogLine;
        public event EventHandler<BridgeProgressEventArgs> ProgressChanged;
        public event EventHandler<BridgeExecutionCompletedEventArgs> Completed;
        public event EventHandler<BridgeTaskItemCompletedEventArgs> ItemCompleted;

        public BridgeTaskCoordinator()
        {
            executionCore = new BridgeExecutionCore();
            executionCore.LogLine += OnExecutionLogLine;
            executionCore.ProgressChanged += OnExecutionProgressChanged;
            executionCore.Completed += OnExecutionCompleted;
        }

        public BridgeTaskState State
        {
            get
            {
                lock (sync) { return state; }
            }
        }

        public bool IsActive
        {
            get
            {
                lock (sync)
                {
                    return state != BridgeTaskState.Idle || (executionCore != null && executionCore.IsRunning);
                }
            }
        }

        public void Start(BridgeTaskRequest task)
        {
            if (task == null) throw new ArgumentNullException("task");
            if (task.ExecutionRequest == null) throw new ArgumentException("ExecutionRequest is required.", "task");
            NativeProcessPolicy.ValidateRequest(task.ExecutionRequest);
            task.NativeBackend = true;

            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("BridgeTaskCoordinator");
                if (state != BridgeTaskState.Idle || executionCore == null || executionCore.IsRunning)
                    throw new InvalidOperationException("A Bridge task is already running.");
                activeTask = task;
                state = BridgeTaskState.Running;
            }
            RaiseStateChanged(BridgeTaskState.Running, task);

            try
            {
                lock (sync)
                {
                    // Running notifications may synchronously cancel or dispose the coordinator.
                    if (disposed || state == BridgeTaskState.Cancelling)
                    {
                        BridgeExecutionCore.CleanupTemporaryFiles(task.ExecutionRequest);
                        OnExecutionCompleted(this, new BridgeExecutionCompletedEventArgs(new BridgeExecutionResult(-1, true, null)));
                        return;
                    }
                    executionCore.Start(task.ExecutionRequest);
                }
            }
            catch(Exception ex)
            {
                RaiseItemCompleted(task,new BridgeExecutionResult(-1,false,ex));
                lock (sync)
                {
                    if (object.ReferenceEquals(activeTask, task)) activeTask = null;
                    state = BridgeTaskState.Idle;
                }
                RaiseStateChanged(BridgeTaskState.Idle, task);
                throw;
            }
        }

        // Requests are prepared independently. Fail fast; successful previous outputs remain.
        public void StartQueue(IList<BridgeTaskRequest> tasks)
        {
            if (tasks == null || tasks.Count == 0) throw new ArgumentException("Queue is empty.");
            foreach(var task in tasks) { if(task==null)throw new ArgumentException("Queue task is missing."); NativeProcessPolicy.ValidateRequest(task.ExecutionRequest); }
            lock (sync)
            {
                if (disposed || state != BridgeTaskState.Idle || executionCore.IsRunning)
                    throw new InvalidOperationException("A task is already active.");
                pending.Clear();
                for (int i = 0; i < tasks.Count; i++)
                {
                    tasks[i].QueueIndex = i + 1; tasks[i].QueueTotal = tasks.Count;
                    if (i > 0) pending.Enqueue(tasks[i]);
                }
                try { Start(tasks[0]); }
                catch { CleanupPending(); throw; }
            }
        }

        private void CleanupPending()
        {
            while (pending.Count > 0)
                BridgeExecutionCore.CleanupTemporaryFiles(pending.Dequeue().ExecutionRequest);
        }

        public void Cancel()
        {
            BridgeTaskRequest task;
            lock (sync)
            {
                if (disposed || executionCore == null || state == BridgeTaskState.Idle || state == BridgeTaskState.Cancelling) return;
                state = BridgeTaskState.Cancelling;
                task = activeTask;
            }
            RaiseStateChanged(BridgeTaskState.Cancelling, task);
            executionCore.Cancel();
        }

        private void OnExecutionLogLine(object sender, BridgeLogLineEventArgs e)
        {
            EventHandler<BridgeLogLineEventArgs> handler = LogLine;
            if (handler != null) handler(this, e);
        }

        private void OnExecutionProgressChanged(object sender, BridgeProgressEventArgs e)
        {
            if (State == BridgeTaskState.Cancelling) return;
            EventHandler<BridgeProgressEventArgs> handler = ProgressChanged;
            BridgeTaskRequest task;
            lock (sync) { task = activeTask; }
            if (task != null && task.QueueTotal > 1)
                e = new BridgeProgressEventArgs(e.HasProgress,
                    ((task.QueueIndex - 1) * 1000 + e.ProgressPermille) / task.QueueTotal,
                    e.HasMetrics, e.FpsText, e.SpeedText, e.EtaText, e.HasStage, e.StageCurrent, e.StageTotal,
                    "[Queue " + task.QueueIndex + "/" + task.QueueTotal + "] " + e.StageText);
            if (e.HasProgress && e.ProgressPermille >= 1000)
                e = new BridgeProgressEventArgs(true, 999, e.HasMetrics, e.FpsText, e.SpeedText, e.EtaText,
                    e.HasStage, e.StageCurrent, e.StageTotal, e.StageText);
            if (handler != null) handler(this, e);
        }

        private void OnExecutionCompleted(object sender, BridgeExecutionCompletedEventArgs e)
        {
            BridgeTaskRequest task;
            BridgeExecutionResult result = e.Result;
            lock (sync)
            {
                task = activeTask;
                if (task != null && task.QueueTotal > 1)
                    OnExecutionLogLine(this, new BridgeLogLineEventArgs(BridgeLogStream.System,
                        "[Queue] item " + task.QueueIndex + "/" + task.QueueTotal +
                        " exit=" + result.ExitCode + " cancelled=" + result.Cancelled));
                if (state == BridgeTaskState.Cancelling && !result.Cancelled)
                    result = new BridgeExecutionResult(-1, true, result.Exception);
                RaiseItemCompleted(task,result);
                if (!disposed && !result.Cancelled && result.Exception == null && result.ExitCode == 0 && pending.Count > 0)
                {
                    activeTask = pending.Dequeue();
                    RaiseStateChanged(BridgeTaskState.Running, activeTask);
                    try
                    {
                        if (disposed || state == BridgeTaskState.Cancelling)
                        {
                            BridgeExecutionCore.CleanupTemporaryFiles(activeTask == null ? null : activeTask.ExecutionRequest);
                            result = new BridgeExecutionResult(-1, true, null);
                            RaiseItemCompleted(activeTask,result);
                        }
                        else { executionCore.Start(activeTask.ExecutionRequest); return; }
                    }
                    catch (Exception ex)
                    {
                        BridgeExecutionCore.CleanupTemporaryFiles(activeTask.ExecutionRequest);
                        result = new BridgeExecutionResult(-1, false, ex);
                        RaiseItemCompleted(activeTask,result);
                    }
                }
                if (task != null && task.QueueTotal > 1)
                    OnExecutionLogLine(this, new BridgeLogLineEventArgs(BridgeLogStream.System,
                        "[Queue] terminal: skipped=" + pending.Count + " total=" + task.QueueTotal));
                CleanupPending();
                activeTask = null;
                state = BridgeTaskState.Idle;
            }
            RaiseStateChanged(BridgeTaskState.Idle, task);
            EventHandler<BridgeExecutionCompletedEventArgs> handler = Completed;
            if (handler != null) handler(this, new BridgeExecutionCompletedEventArgs(result));
        }

        private void RaiseItemCompleted(BridgeTaskRequest task,BridgeExecutionResult result)
        {
            var handler=ItemCompleted;if(task!=null&&handler!=null)handler(this,new BridgeTaskItemCompletedEventArgs(task,result));
        }

        private void RaiseStateChanged(BridgeTaskState newState, BridgeTaskRequest task)
        {
            EventHandler<BridgeTaskStateChangedEventArgs> handler = StateChanged;
            if (handler != null) handler(this, new BridgeTaskStateChangedEventArgs(newState, task));
        }

        public void Dispose()
        {
            BridgeExecutionCore core;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                CleanupPending();
                core = executionCore;
                executionCore = null;
                activeTask = null;
                state = BridgeTaskState.Idle;
            }
            if (core != null)
            {
                core.LogLine -= OnExecutionLogLine;
                core.ProgressChanged -= OnExecutionProgressChanged;
                core.Completed -= OnExecutionCompleted;
                core.Dispose();
            }
        }
    }
}
