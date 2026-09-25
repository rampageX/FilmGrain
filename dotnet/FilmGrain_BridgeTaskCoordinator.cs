using System;

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

        public event EventHandler<BridgeTaskStateChangedEventArgs> StateChanged;
        public event EventHandler<BridgeLogLineEventArgs> LogLine;
        public event EventHandler<BridgeProgressEventArgs> ProgressChanged;
        public event EventHandler<BridgeExecutionCompletedEventArgs> Completed;

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
                executionCore.Start(task.ExecutionRequest);
            }
            catch
            {
                lock (sync)
                {
                    if (object.ReferenceEquals(activeTask, task)) activeTask = null;
                    state = BridgeTaskState.Idle;
                }
                RaiseStateChanged(BridgeTaskState.Idle, task);
                throw;
            }
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
            EventHandler<BridgeProgressEventArgs> handler = ProgressChanged;
            if (handler != null) handler(this, e);
        }

        private void OnExecutionCompleted(object sender, BridgeExecutionCompletedEventArgs e)
        {
            BridgeTaskRequest task;
            lock (sync)
            {
                task = activeTask;
                activeTask = null;
                state = BridgeTaskState.Idle;
            }
            RaiseStateChanged(BridgeTaskState.Idle, task);
            EventHandler<BridgeExecutionCompletedEventArgs> handler = Completed;
            if (handler != null) handler(this, e);
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
