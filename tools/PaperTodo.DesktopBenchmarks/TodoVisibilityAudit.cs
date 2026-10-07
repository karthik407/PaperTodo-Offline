using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo;

// Temporary A/B measurement. This file and the two Program entry points are identical
// on the base and candidate. Product access uses only APIs/fields present on the base.
internal static partial class Program
{
    private const string TodoVisibilityAuditMarker = ".papertodo-todo-visibility-audit";
    private const string TodoVisibilityAuditResult = "todo-visibility-fixture.json";
    private const BindingFlags TodoVisibilityPrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    private static int RunTodoVisibilityAudit(string outputPath)
    {
        try
        {
            var samples = new List<JsonElement>();
            foreach (var rows in new[] { 100, 200 })
            foreach (var links in new[] { 0, 2 })
            {
                var directory = Path.Combine(Path.GetTempPath(), "PaperTodo.DesktopBenchmarks", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    CopyBinaries(AppContext.BaseDirectory, directory);
                    File.WriteAllText(Path.Combine(directory, TodoVisibilityAuditMarker), "owned measurement data");
                    var start = new ProcessStartInfo(Path.Combine(directory, "PaperTodo.DesktopBenchmarks.exe"))
                    {
                        WorkingDirectory = directory, UseShellExecute = false,
                        RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
                    };
                    start.ArgumentList.Add("--todo-visibility-audit-fixture");
                    start.ArgumentList.Add(rows.ToString());
                    start.ArgumentList.Add(links.ToString());
                    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Todo fixture.");
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(120_000))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit();
                        throw new TimeoutException($"Todo visibility fixture did not finish: {rows} rows, {links} links.");
                    }
                    Console.Write(stdout.GetAwaiter().GetResult());
                    Console.Error.Write(stderr.GetAwaiter().GetResult());
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException("Todo visibility fixture failed: " + process.ExitCode);
                    using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, TodoVisibilityAuditResult)));
                    samples.Add(result.RootElement.Clone());
                }
                finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } }
            }

            var destination = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                framework = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                capturedUtc = DateTimeOffset.UtcNow,
                scope = "Real ShowAllPapers/HideAllPapers; 5 Todo windows plus 2 Note targets; animations disabled; one warm cycle, five measured cycles.",
                timing = "UI call wall time and allocation; through-ApplicationIdle time includes queued WPF work. Neither measures DWM presentation or scanout FPS. No forced layout/render or forced GC in measured operations.",
                scenarios = samples
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("TODO_VISIBILITY_AUDIT " + destination);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static int TodoVisibilityAuditFixture(int rowCount, int linkCount)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, TodoVisibilityAuditMarker)))
            throw new InvalidOperationException("Refusing to measure against non-fixture user data.");
        var result = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var state = new AppState
                {
                    TelemetryEnabled = false, EnableAnimations = false,
                    UseCapsuleMode = true, UseDeepCapsuleMode = false,
                    EnableTodoPaperLinks = true, HideLinkedPapersFromCapsules = true,
                    ShowLinkedPaperName = false, RunLinkedScriptCapsulesOnClick = false,
                    ExperimentalEdgeCapsuleHoverPreview = false,
                    UsePersistentPowerShellProcess = false, McpEnabled = false,
                    FullscreenTopmostMode = FullscreenTopmostModes.StayOnTop,
                    Theme = "light", PaperSkin = PaperSkins.Paper
                };
                var area = SystemParameters.WorkArea;
                for (var target = 0; target < 2; target++) state.Papers.Add(new PaperData
                {
                    Id = "target-" + target, Type = PaperTypes.Note, Content = "short target " + target,
                    IsVisible = true, IsCollapsed = false,
                    X = area.Left + 40 + target * 10, Y = area.Top + 40 + target * 10,
                    Width = 380, Height = 330
                });
                for (var index = 0; index < 5; index++)
                {
                    var paper = new PaperData
                    {
                        Id = "todo-" + index, Type = PaperTypes.Todo,
                        IsVisible = true, IsCollapsed = false,
                        X = area.Left + 70 + index * 12, Y = area.Top + 70 + index * 12,
                        Width = 420, Height = 360
                    };
                    for (var row = 0; row < rowCount; row++)
                    {
                        var item = new PaperItem
                        {
                            Id = $"todo-{index}-row-{row}", Order = row,
                            Text = $"Task {row + 1}: retain the editor during bulk visibility changes."
                        };
                        if (row < linkCount) item.LinkPaper("target-" + row);
                        paper.Items.Add(item);
                    }
                    state.Papers.Add(paper);
                }
                var store = new StateStore();
                store.SaveJsonSync(store.SerializeState(state), 1);
                using var controller = new AppController();
                await controller.StartAsync(createDefaultPaper: false);
                await TodoVisibilityIdle();
                var windows = (Dictionary<string, PaperWindow>)TodoVisibilityField(controller, "_windows");
                TodoVisibilityRequire(windows.Count == 7 && windows.Values.All(window => window.HasExpandedPaperSurface),
                    "Startup did not restore all fixture windows.");
                var todos = controller.State.Papers.Where(paper => paper.Type == PaperTypes.Todo).ToArray();

                controller.HideAllPapers();
                await TodoVisibilityIdle();
                controller.ShowAllPapers();
                await TodoVisibilityIdle();

                var operations = new List<TodoVisibilityOperation>();
                for (var cycle = 0; cycle < 5; cycle++)
                {
                    operations.Add(await Measure("hide", cycle, controller.HideAllPapers, expectedVisible: false));
                    operations.Add(await Measure("show", cycle, controller.ShowAllPapers, expectedVisible: true));
                }
                var fixture = new
                {
                    todoWindows = todos.Length, noteTargets = 2, rowsPerTodo = rowCount,
                    linkedRowsPerTodo = linkCount, ordinaryRowsPerTodo = rowCount - linkCount,
                    warmupCycles = 1, measuredCycles = 5,
                    operations
                };
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, TodoVisibilityAuditResult),
                    JsonSerializer.Serialize(fixture, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"TODO_VISIBILITY_FIXTURE rows={rowCount} links={linkCount} operations={operations.Count}");

                async Task<TodoVisibilityOperation> Measure(string actionName, int cycle, Action action, bool expectedVisible)
                {
                    var before = todos.ToDictionary(paper => paper.Id, paper => TodoVisibilitySnapshot.Capture(windows[paper.Id]));
                    var threadId = Environment.CurrentManagedThreadId;
                    var gc0 = GC.CollectionCount(0);
                    var gc1 = GC.CollectionCount(1);
                    var gc2 = GC.CollectionCount(2);
                    var allocation = GC.GetAllocatedBytesForCurrentThread();
                    var began = Stopwatch.GetTimestamp();
                    action();
                    var returned = Stopwatch.GetTimestamp();
                    var callAllocation = GC.GetAllocatedBytesForCurrentThread() - allocation;
                    await TodoVisibilityIdle();
                    var idle = Stopwatch.GetTimestamp();
                    var throughIdleAllocation = GC.GetAllocatedBytesForCurrentThread() - allocation;
                    var gen0Collections = GC.CollectionCount(0) - gc0;
                    var gen1Collections = GC.CollectionCount(1) - gc1;
                    var gen2Collections = GC.CollectionCount(2) - gc2;
                    TodoVisibilityRequire(Environment.CurrentManagedThreadId == threadId,
                        "Measurement resumed outside the original WPF UI thread.");
                    TodoVisibilityRequire(controller.State.Papers.All(paper => paper.IsVisible == expectedVisible) &&
                        windows.Values.All(window => window.HasExpandedPaperSurface == expectedVisible),
                        "The visibility command did not reach its expected window/model state.");

                    var retention = new List<TodoVisibilityRetention>();
                    foreach (var paper in todos)
                    {
                        var previous = before[paper.Id];
                        var current = TodoVisibilitySnapshot.Capture(windows[paper.Id]);
                        TodoVisibilityRequire(previous.Rows.Count == rowCount && current.Rows.Count == rowCount &&
                            previous.Editors.Count == rowCount && current.Editors.Count == rowCount,
                            "Visibility changed the number of Todo rows or editors.");
                        var ordinaryIds = paper.Items.Where(item => item.LinkedPaperId == null).Select(item => item.Id).ToArray();
                        retention.Add(new TodoVisibilityRetention(
                            paper.Id, current.Rows.Count,
                            previous.Rows.Count(pair => current.Rows.TryGetValue(pair.Key, out var row) && ReferenceEquals(row, pair.Value)),
                            previous.Editors.Count(pair => current.Editors.TryGetValue(pair.Key, out var editor) && ReferenceEquals(editor, pair.Value)),
                            ordinaryIds.Length,
                            ordinaryIds.Count(id => ReferenceEquals(previous.Rows[id], current.Rows[id])),
                            ordinaryIds.Count(id => ReferenceEquals(previous.Editors[id], current.Editors[id])),
                            current.Generation - previous.Generation));
                    }
                    return new TodoVisibilityOperation(
                        actionName, cycle,
                        Stopwatch.GetElapsedTime(began, returned).TotalMilliseconds,
                        Stopwatch.GetElapsedTime(began, idle).TotalMilliseconds,
                        callAllocation, throughIdleAllocation,
                        gen0Collections, gen1Collections, gen2Collections,
                        expectedVisible, true, retention);
                }
            }
            catch (Exception error) { result = 1; Console.Error.WriteLine(error); }
            finally { app.Shutdown(); }
        });
        app.Run();
        return result;
    }

    private static Task TodoVisibilityIdle() =>
        Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).Task;

    private static object TodoVisibilityField(object owner, string field) =>
        owner.GetType().GetField(field, TodoVisibilityPrivate)!.GetValue(owner)!;

    private static void TodoVisibilityRequire(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record TodoVisibilitySnapshot(int Generation, Dictionary<string, object> Rows, Dictionary<string, object> Editors)
    {
        internal static TodoVisibilitySnapshot Capture(PaperWindow window)
        {
            var editors = (IDictionary)TodoVisibilityField(window, "_todoEditors");
            return new(
                (int)TodoVisibilityField(window, "_todoRowsGeneration"),
                ((IEnumerable)TodoVisibilityField(window, "_todoRows")).Cast<Border>().ToDictionary(row => (string)row.Tag, row => (object)row),
                editors.Keys.Cast<string>().ToDictionary(id => id, id => editors[id]!));
        }
    }

    private sealed record TodoVisibilityOperation(
        string Action, int Cycle, double UiCallMs, double ThroughApplicationIdleMs,
        long UiCallAllocatedBytes, long UiAllocatedThroughIdleBytes,
        int Gen0Collections, int Gen1Collections, int Gen2Collections,
        bool ExpectedVisible, bool VisibilityInvariantPassed, List<TodoVisibilityRetention> Retention);

    private sealed record TodoVisibilityRetention(
        string PaperId, int RowCount, int RowsRetained, int EditorsRetained,
        int OrdinaryRowCount, int OrdinaryRowsRetained, int OrdinaryEditorsRetained,
        int RowGenerationDelta);
}
