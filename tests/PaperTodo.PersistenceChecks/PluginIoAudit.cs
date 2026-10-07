using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using PaperTodo;

internal static class PluginIoAudit
{
    private const int ObservationMilliseconds = 400;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    public static int Run(string outputPath)
    {
        using var ui = new AuditUiThread();
        _ = Measure(ui, "same-provider-input", 0);
        var measurements = new List<Measurement>();
        for (var iteration = 1; iteration <= 3; iteration++)
        {
            measurements.Add(Measure(ui, "same-provider-input", iteration));
            measurements.Add(Measure(ui, "other-provider-input", iteration));
            measurements.Add(Measure(ui, "paper-state-deletion", iteration));
        }

        var result = new
        {
            Audit = "plugin-ui-access-during-controlled-background-write",
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            ObservationMilliseconds,
            Method = "The actual atomic writer is paused after temp-file write. A WPF Dispatcher callback then reads/mutates warm plugin state. The observer releases the writer after the callback and follow-up input complete, or one 400 ms observation window expires, independently of the UI. These are controlled contention measurements, not natural disk or FPS measurements.",
            Measurements = measurements
        };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, json);
        Console.WriteLine(json);
        return measurements.All(value => value.FinalStateCorrect && value.MaximumConcurrentWriters == 1) ? 0 : 1;
    }

    private static Measurement Measure(AuditUiThread ui, string scenario, int iteration)
    {
        var root = Path.Combine(Path.GetTempPath(), "PaperTodo-plugin-io-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var writer = new PausedAtomicWriter();
        var store = new PaperBodyPluginDataStore(root, writer, 25, 10_000);
        using var started = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        using var heartbeat = new ManualResetEventSlim();
        Exception? error = null;
        var actionMilliseconds = 0.0;
        var stateCorrectInCallback = false;
        DispatcherOperation? actionOperation = null;
        DispatcherOperation? heartbeatOperation = null;
        try
        {
            ui.Dispatcher.Invoke(() =>
            {
                _ = store.ReadPaperState("audit.slow", "paper");
                _ = store.ReadPaperState("audit.other", "paper");
            });
            store.SavePaperState("audit.slow", "paper", 1, "{\"text\":\"old\"}");
            Require(writer.Entered.Wait(Deadline), "background writer did not enter");

            actionOperation = ui.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                started.Set();
                try
                {
                    switch (scenario)
                    {
                        case "same-provider-input":
                            Require(store.ReadPaperState("audit.slow", "paper").Json.Contains("old"), "warm read lost old state");
                            store.SavePaperState("audit.slow", "paper", 1, "{\"text\":\"new\"}");
                            stateCorrectInCallback = store.ReadPaperState("audit.slow", "paper").Json.Contains("new");
                            break;
                        case "other-provider-input":
                            store.SavePaperState("audit.other", "paper", 1, "{\"text\":\"other-new\"}");
                            stateCorrectInCallback = store.ReadPaperState("audit.other", "paper").Json.Contains("other-new");
                            break;
                        case "paper-state-deletion":
                            store.RemovePaperStateEverywhere("paper");
                            stateCorrectInCallback = !store.TryReadPaperState("audit.slow", "paper", out _);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(scenario));
                    }
                }
                catch (Exception exception)
                {
                    error = exception;
                }
                finally
                {
                    actionMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                    completed.Set();
                }
            }));
            heartbeatOperation = ui.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(heartbeat.Set));
            Require(started.Wait(Deadline), "UI callback did not start");
            var observation = Stopwatch.StartNew();
            var finishedBeforeRelease = completed.Wait(ObservationMilliseconds);
            var remainingObservation = Math.Max(0, ObservationMilliseconds - (int)observation.ElapsedMilliseconds);
            var heartbeatBeforeRelease = finishedBeforeRelease && heartbeat.Wait(remainingObservation);
            writer.Release.Set();
            Require(completed.Wait(Deadline), "UI callback did not complete after writer release");
            Require(heartbeat.Wait(Deadline), "UI Dispatcher did not process follow-up input");
            if (error != null)
            {
                throw new InvalidOperationException("UI audit callback failed", error);
            }
            Require(!writer.PauseTimedOut, "writer timeout invalidated the controlled observation");

            // Normal disposal is intentionally a durable final-flush boundary in both versions.
            store.Dispose();
            var slow = ReadDocument(Path.Combine(root, "data", "audit.slow.json"));
            var durableCorrect = scenario switch
            {
                "same-provider-input" => slow.GetProperty("papers").GetProperty("paper").GetProperty("data").GetProperty("text").GetString() == "new",
                "other-provider-input" => ReadDocument(Path.Combine(root, "data", "audit.other.json")).GetProperty("papers").GetProperty("paper").GetProperty("data").GetProperty("text").GetString() == "other-new",
                "paper-state-deletion" => !slow.GetProperty("papers").TryGetProperty("paper", out _),
                _ => false
            };
            return new Measurement(scenario, iteration, finishedBeforeRelease, heartbeatBeforeRelease,
                actionMilliseconds, stateCorrectInCallback && durableCorrect, writer.Writes, writer.MaximumConcurrentWriters);
        }
        finally
        {
            writer.Release.Set();
            if (actionOperation != null &&
                !actionOperation.Abort() &&
                actionOperation.Status != DispatcherOperationStatus.Aborted)
            {
                Require(completed.Wait(Deadline), "UI callback did not finish during cleanup");
            }
            if (heartbeatOperation != null &&
                !heartbeatOperation.Abort() &&
                heartbeatOperation.Status != DispatcherOperationStatus.Aborted)
            {
                Require(heartbeat.Wait(Deadline), "UI follow-up input did not finish during cleanup");
            }
            store.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonElement ReadDocument(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Measurement(string Scenario, int Iteration,
        bool UiCompletedBeforeWriterRelease, bool FollowUpInputBeforeWriterRelease,
        double UiActionMilliseconds, bool FinalStateCorrect, int AtomicWrites,
        int MaximumConcurrentWriters);

    private sealed class PausedAtomicWriter : IDurableAtomicFileWriter, IDisposable
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        private readonly DurableAtomicFileWriter _inner;
        private int _stages;
        private int _active;
        private int _writes;
        private int _maximumConcurrentWriters;
        private int _pauseTimedOut;

        public PausedAtomicWriter()
        {
            _inner = new DurableAtomicFileWriter((stage, _) =>
            {
                if (stage != DurableAtomicWriteStage.AfterTempWrite || Interlocked.Increment(ref _stages) != 1) return;
                Entered.Set();
                if (!Release.Wait(Deadline))
                {
                    Volatile.Write(ref _pauseTimedOut, 1);
                    throw new TimeoutException("observer did not release the blocked writer");
                }
            });
        }

        public int Writes => Volatile.Read(ref _writes);
        public int MaximumConcurrentWriters => Volatile.Read(ref _maximumConcurrentWriters);
        public bool PauseTimedOut => Volatile.Read(ref _pauseTimedOut) != 0;

        public void Write(string targetPath, byte[] bytes, Func<string, bool>? validateTemp = null)
        {
            var active = Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _writes);
            int previous;
            do
            {
                previous = Volatile.Read(ref _maximumConcurrentWriters);
                if (previous >= active) break;
            } while (Interlocked.CompareExchange(ref _maximumConcurrentWriters, active, previous) != previous);
            try { _inner.Write(targetPath, bytes, validateTemp); }
            finally { Interlocked.Decrement(ref _active); }
        }

        public void Dispose()
        {
            Release.Set();
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private sealed class AuditUiThread : IDisposable
    {
        private readonly Thread _thread;
        public Dispatcher Dispatcher { get; }

        public AuditUiThread()
        {
            using var ready = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;
            _thread = new Thread(() =>
            {
                dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                ready.Set();
                System.Windows.Threading.Dispatcher.Run();
            }) { IsBackground = true, Name = "Plugin I/O audit UI" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Require(ready.Wait(Deadline), "UI thread did not start");
            Dispatcher = dispatcher!;
        }

        public void Dispose()
        {
            Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            Require(_thread.Join(Deadline), "UI thread did not stop");
        }
    }
}
