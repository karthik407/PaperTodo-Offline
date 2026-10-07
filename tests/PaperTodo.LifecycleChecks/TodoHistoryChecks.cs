using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static class TodoHistoryChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task Run()
    {
        var state = new AppState
        {
            TelemetryEnabled = false, EnableAnimations = false,
            UseCapsuleMode = false, UseDeepCapsuleMode = false,
            EnableTodoPaperLinks = true, HideLinkedPapersFromCapsules = false,
            ShowTodoBottomBar = false, AutoClearCompletedTodos = false,
            AutoMoveCompletedTodosToBottom = false,
            ExperimentalTodoReminders = true, ExperimentalEdgeCapsuleHoverPreview = false,
            UsePersistentPowerShellProcess = false, McpEnabled = false,
            FullscreenTopmostMode = FullscreenTopmostModes.StayOnTop,
            Theme = "light", PaperSkin = PaperSkins.Paper
        };
        var area = SystemParameters.WorkArea;
        state.Papers.Add(new PaperData
        {
            Id = "history", Type = PaperTypes.Todo,
            IsVisible = true, IsCollapsed = false,
            X = area.Left + 60, Y = area.Top + 60, Width = 520, Height = 390,
            Items = Items(12)
        });
        state.Papers.Add(new PaperData
        {
            Id = "link-target", Type = PaperTypes.Note, Content = "target",
            IsVisible = false, IsCollapsed = false,
            X = area.Left + 600, Y = area.Top + 60, Width = 300, Height = 240
        });
        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(state), 1);
        using var controller = new AppController();
        await controller.StartAsync(createDefaultPaper: false);
        await Idle();
        var paper = controller.State.Papers.Single(value => value.Id == "history");
        var windows = (Dictionary<string, PaperWindow>)Field(controller, "_windows");
        var window = windows[paper.Id];
        var panel = InstallCountingPanel(window);
        await Idle();

        // Ordinary single-paper lists must preserve live editors and focus even when the
        // no-move path skips the LIS. Also cover append-area synchronization on that path.
        foreach (var count in new[] { 1, 5, 10 })
        {
            await Reset(count);
            var unchanged = Capture(window, paper);
            var focused = Editors(window)["row-0"];
            focused.Select(2, 4);
            var caret = (focused.SelectionStart, focused.SelectionLength, focused.CaretIndex);
            panel.ResetCounts();
            ReconcileRows(window);
            await Idle();
            RequireVisualChanges(panel, 0, 0, $"{count}-row unchanged refresh");
            RequireRetained(unchanged, window, paper, [], $"{count}-row unchanged refresh");
            RequireSelection(focused, caret, $"{count}-row unchanged refresh");
            Require(focused.IsKeyboardFocused, "unchanged small-list refresh lost keyboard focus");
            controller.State.ShowTodoBottomBar = true;
            ReconcileRows(window);
            Require(panel.Children.Count == count + 1, "no-move refresh failed to enable append area");
            controller.State.ShowTodoBottomBar = false;
            ReconcileRows(window);
            RequireNoAppendArea(window);
        }
        await Reset(12);

        // Pure reorder changes list position, not the models captured by row event handlers.
        var originalOrder = paper.Items.Select(item => item.Id).ToArray();
        var move = typeof(PaperWindow).GetMethod("MoveItems", Private)!;
        var after = Enum.Parse(move.GetParameters()[2].ParameterType, "After");
        panel.ResetCounts();
        move.Invoke(window, [new[] { "row-0" }, "row-11", after, "row-0"]);
        await Idle();
        RequireVisualChanges(panel, 1, 1, "first-to-last drag");
        var editor = Editors(window)["row-0"];
        await Focus(window, editor);
        editor.Select(3, 6);
        var selection = (editor.SelectionStart, editor.SelectionLength, editor.CaretIndex);
        var before = Capture(window, paper);
        panel.ResetCounts();
        await SendKey(window, Key.Z);
        RequireVisualChanges(panel, 1, 1, "last-to-first undo");
        Require(paper.Items.Select(item => item.Id).SequenceEqual(originalOrder), "reorder undo did not restore list order");
        RequireRetained(before, window, paper, [], "reorder undo");
        RequireSelection(editor, selection, "reorder undo");
        Require(editor.IsKeyboardFocused, "reorder undo lost actual keyboard focus on the moved row");
        before = Capture(window, paper);
        panel.ResetCounts();
        await SendKey(window, Key.Y);
        RequireVisualChanges(panel, 1, 1, "first-to-last redo");
        Require(paper.Items[^1].Id == "row-0", "reorder redo did not restore the moved row");
        RequireRetained(before, window, paper, [], "reorder redo");
        RequireSelection(editor, selection, "reorder redo");
        Require(editor.IsKeyboardFocused, "reorder redo lost actual keyboard focus on the moved row");
        RequireHistoryIsolation(window, paper);

        // Retained editor and checkbox closures must keep writing to the current live items.
        editor.Select(editor.Text.Length, 0);
        editor.SelectedText = " live edit";
        Require(paper.Items.Single(item => item.Id == "row-0").Text == editor.Text,
            "retained editor wrote to a detached snapshot item");
        CheckBox(window, "row-1").IsChecked = true;
        Require(paper.Items.Single(item => item.Id == "row-1").Done,
            "retained checkbox wrote to a detached snapshot item");
        RequireHistoryIsolation(window, paper);

        // Count real WPF visual parent changes, not just retained object identities. Moving
        // a short group keeps the longer unchanged group attached in either direction.
        await Reset(8);
        before = Capture(window, paper);
        panel.ResetCounts();
        move.Invoke(window, [new[] { "row-0", "row-1" }, "row-7", after, "row-0"]);
        await Idle();
        RequireVisualChanges(panel, 2, 2, "group drag");
        Require(paper.Items.Select(item => item.Id).SequenceEqual(["row-2", "row-3", "row-4", "row-5", "row-6", "row-7", "row-0", "row-1"]),
            "group drag did not retain the moved group's relative order");
        RequireRetained(before, window, paper, [], "group drag");
        before = Capture(window, paper);
        panel.ResetCounts();
        await SendKey(window, Key.Z);
        RequireVisualChanges(panel, 2, 2, "group undo");
        RequireRetained(before, window, paper, [], "group undo");
        panel.ResetCounts();
        await SendKey(window, Key.Y);
        RequireVisualChanges(panel, 2, 2, "group redo");
        RequireRetained(before, window, paper, [], "group redo");

        // Arbitrary target orders exercise reconciliation independently of drag setup.
        // Reversing all rows needs n-1 moves; an unchanged order needs none.
        await Reset(6);
        before = Capture(window, paper);
        panel.ResetCounts();
        ReconcileRows(window);
        RequireVisualChanges(panel, 0, 0, "unchanged order");
        Invoke(window, "PushUndoSnapshot");
        paper.Items.Reverse();
        panel.ResetCounts();
        ReconcileRows(window);
        await Idle();
        RequireVisualChanges(panel, 5, 5, "reversal");
        RequireRetained(before, window, paper, [], "reversal");
        await Focus(window, Editors(window)["row-0"]);
        panel.ResetCounts();
        await SendKey(window, Key.Z);
        RequireVisualChanges(panel, 5, 5, "reversal undo");
        Require(paper.Items.Select(item => item.Id).SequenceEqual(Items(6).Select(item => item.Id)),
            "reversal undo did not restore the original order");
        RequireRetained(before, window, paper, [], "reversal undo");

        // Mixed removals, a changed row and an insertion use actual remaining visual
        // positions. The enabled append area stays attached at the end throughout.
        await Reset(8);
        controller.State.ShowTodoBottomBar = true;
        Invoke(window, "SyncTodoAppendArea");
        await Idle();
        var appendArea = (Border)Field(window, "_appendArea");
        var oldItems = paper.Items.ToArray();
        before = Capture(window, paper);
        Invoke(window, "PushUndoSnapshot");
        var changedItem = TodoRules.Clone(oldItems[2]);
        changedItem.Text += " rebuilt";
        paper.Items = [oldItems[6], new PaperItem { Id = "inserted", Text = "inserted row" }, oldItems[0], changedItem, oldItems[3], oldItems[7], oldItems[4]];
        panel.ResetCounts();
        ReconcileRows(window, ["row-2"]);
        await Idle();
        RequireVisualChanges(panel, 4, 5, "mixed reconciliation");
        RequireRetained(before, window, paper, ["row-2"], "mixed reconciliation", appendArea: true);
        Require(!panel.Removed.Contains(appendArea), "mixed reconciliation detached the append area");
        await Focus(window, Editors(window)["row-6"]);
        before = Capture(window, paper);
        panel.ResetCounts();
        await SendKey(window, Key.Z);
        RequireVisualChanges(panel, 5, 4, "mixed undo");
        Require(paper.Items.Select(item => item.Id).SequenceEqual(Items(8).Select(item => item.Id)),
            "mixed undo did not restore removed rows and their order");
        RequireRetained(before, window, paper, ["row-2"], "mixed undo", appendArea: true);
        Require(!panel.Removed.Contains(appendArea), "mixed undo detached the append area");
        before = Capture(window, paper);
        panel.ResetCounts();
        await SendKey(window, Key.Y);
        RequireVisualChanges(panel, 4, 5, "mixed redo");
        Require(paper.Items.Select(item => item.Id).SequenceEqual(["row-6", "inserted", "row-0", "row-2", "row-3", "row-7", "row-4"]),
            "mixed redo did not restore the inserted row and target order");
        RequireRetained(before, window, paper, ["row-2"], "mixed redo", appendArea: true);
        Require(!panel.Removed.Contains(appendArea) && ReferenceEquals(Field(window, "_appendArea"), appendArea),
            "mixed redo replaced or detached the append area");
        RequireHistoryIsolation(window, paper);

        // Native text undo has priority before list history. A list history boundary then
        // clears native history on retained controls, as the former full rebuild did.
        await Reset(4);
        editor = Editors(window)["row-0"];
        await Focus(window, editor);
        var originalText = editor.Text;
        editor.Select(editor.Text.Length, 0);
        editor.SelectedText = " human";
        Require(editor.CanUndo, "native text undo fixture did not record an edit");
        await SendKey(window, Key.Z, expectPaperHistory: false);
        Require(editor.Text == originalText && History(window, "_undoStack").Count == 0,
            "Ctrl+Z replayed list history ahead of native text undo");
        await SendKey(window, Key.Y, expectPaperHistory: false);
        Require(editor.Text == originalText + " human" && History(window, "_undoStack").Count == 0,
            "Ctrl+Y replayed list history ahead of native text redo");
        await Focus(window, Editors(window)["row-1"]);
        CheckBox(window, "row-1").IsChecked = true;
        Require(History(window, "_undoStack").Count == 2, "manual edit and checkbox did not form two history steps");
        before = Capture(window, paper);
        await SendKey(window, Key.Z);
        RequireRetained(before, window, paper, ["row-1"], "checkbox undo");
        Require(!editor.CanUndo && !editor.CanRedo, "list undo left stale native editor history on a retained row");
        Require(!paper.Items[1].Done && paper.Items[0].Text == originalText + " human",
            "checkbox undo also replayed the earlier human edit");
        await SendKey(window, Key.Z);
        Require(paper.Items[0].Text == originalText, "second list undo did not restore the earlier human edit");
        await SendKey(window, Key.Y);
        await SendKey(window, Key.Y);
        Require(paper.Items[1].Done && paper.Items[0].Text == originalText + " human",
            "redo did not preserve human edit then checkbox ordering");
        RequireHistoryIsolation(window, paper);

        // Every persisted row presentation field must invalidate the affected row. The offset
        // case deliberately has the same UTC instant, but a different persisted DateTimeOffset.
        var reminder = DateTimeOffset.UtcNow.AddDays(30);
        (string Name, Action<PaperItem>? Prepare, Action<PaperItem> Change)[] fields =
        [
            ("text", null, item => item.Text += " changed"),
            ("done", null, item => item.Done = true),
            ("linked paper", null, item => item.LinkPaper("link-target")),
            ("linked path", null, item => item.LinkPath(AppContext.BaseDirectory, true)),
            ("path kind", item => item.LinkPath(AppContext.BaseDirectory, true), item => item.LinkPath(AppContext.BaseDirectory, false)),
            ("reminder time", null, item => item.ReminderAt = reminder),
            ("reminder offset", item => item.ReminderAt = reminder, item => item.ReminderAt = reminder.ToOffset(TimeSpan.FromHours(8))),
            ("reminder delivered", item => item.ReminderAt = reminder, item => item.ReminderTriggered = true)
        ];
        foreach (var (name, prepare, change) in fields)
        {
            await Reset(4, prepare);
            var previous = TodoRules.Clone(paper.Items[0]);
            Invoke(window, "PushUndoSnapshot");
            change(paper.Items[0]);
            var changed = TodoRules.Clone(paper.Items[0]);
            window.RefreshTodoRowsForExternalChange();
            await Idle();
            await Focus(window, Editors(window)["row-1"]);
            before = Capture(window, paper);
            await SendKey(window, Key.Z);
            RequireRetained(before, window, paper, ["row-0"], name + " undo");
            RequireItem(paper.Items[0], previous, name + " undo model");
            before = Capture(window, paper);
            await SendKey(window, Key.Y);
            RequireRetained(before, window, paper, ["row-0"], name + " redo");
            RequireItem(paper.Items[0], changed, name + " redo model");
            RequireHistoryIsolation(window, paper);
        }

        // A successful external mutation orders a pending human edit before the external step.
        await Reset(4);
        editor = Editors(window)["row-0"];
        await Focus(window, editor);
        originalText = editor.Text;
        editor.Select(editor.Text.Length, 0);
        editor.SelectedText = " pending human";
        var externalOriginal = paper.Items[1].Text;
        controller.PaperCommands.UpdateTodo(new UpdateTodoRequest
        {
            PaperId = paper.Id, TodoId = "row-1", Text = "external change"
        }, PaperOperationContext.Mcp());
        await Idle();
        Require(History(window, "_undoStack").Count == 2, "external write did not separate the pending human edit");
        await SendKey(window, Key.Z);
        Require(paper.Items[1].Text == externalOriginal && paper.Items[0].Text == originalText + " pending human",
            "external undo also removed the preceding human edit");
        await SendKey(window, Key.Z);
        Require(paper.Items[0].Text == originalText, "human edit was not the second undo after an external write");
        await SendKey(window, Key.Y);
        await SendKey(window, Key.Y);
        Require(paper.Items[0].Text == originalText + " pending human" && paper.Items[1].Text == "external change",
            "external redo order or current model was lost");
        RequireHistoryIsolation(window, paper);

        // Enter insertion, last-row deletion and the disabled append area retain their contracts.
        await Reset(4);
        await Focus(window, Editors(window)["row-0"]);
        await SendKey(window, Key.Enter, expectPaperHistory: false, control: false);
        var insertedId = paper.Items[1].Id;
        Require(paper.Items.Count == 5 && Editors(window)[insertedId].IsKeyboardFocused, "Enter did not create and focus a new row");
        before = Capture(window, paper);
        await SendKey(window, Key.Z);
        Require(paper.Items.Count == 4 && paper.Items.All(item => item.Id != insertedId), "insert undo retained the added row");
        RequireRetained(before, window, paper, [], "insert undo");
        await Focus(window, Editors(window)["row-0"]);
        await SendKey(window, Key.Y);
        Require(paper.Items.Count == 5 && paper.Items[1].Id == insertedId, "insert redo changed the new row identity");
        await Reset(1);
        Invoke(window, "RemoveItem", paper.Items[0], true, null, true);
        await Idle();
        var placeholderId = paper.Items[0].Id;
        Require(placeholderId != "row-0" && TodoRules.IsPlaceholder(paper.Items[0]), "last-row deletion did not create a placeholder");
        await Focus(window, Editors(window)[placeholderId]);
        await SendKey(window, Key.Z);
        Require(paper.Items.Count == 1 && paper.Items[0].Id == "row-0", "last-row undo did not restore the original item");
        await Focus(window, Editors(window)["row-0"]);
        await SendKey(window, Key.Y);
        Require(paper.Items.Count == 1 && paper.Items[0].Id == placeholderId, "last-row redo changed the replacement identity");
        RequireNoAppendArea(window);

        // Interrupt the actual deletion animation before its asynchronous completion. Reflection
        // is used only here to make that timing deterministic; keyboard routing is covered above.
        await Reset(4);
        controller.State.EnableAnimations = true;
        Invoke(window, "RemoveItem", paper.Items[0], true, null, true);
        Require(Rows(window).ContainsKey("row-0") && !Rows(window)["row-0"].IsHitTestVisible,
            "deletion fixture did not retain its animating old row");
        Invoke(window, "Undo");
        await Task.Delay(350);
        await Idle();
        Require(paper.Items.Count == 4 && Rows(window)["row-0"].IsHitTestVisible,
            "a late deletion callback removed the restored history row");
        Require(Rows(window)["row-0"].Opacity > .99 &&
                !Rows(window)["row-0"].HasAnimatedProperties,
            "restored row retained its obsolete deletion animation");
        RequireNoAppendArea(window);
        RequireHistoryIsolation(window, paper);
        Console.WriteLine("PASS Todo history: native Ctrl+Z/Y, minimal visual row moves, model identity, all row fields, text-history priority, external ordering, insertion/deletion and animation interruption");

        async Task Reset(int count, Action<PaperItem>? prepare = null)
        {
            controller.State.EnableAnimations = false;
            controller.State.ShowTodoBottomBar = false;
            SetField(window, "_activeOriginalItemId", null);
            SetField(window, "_activeOriginalText", null);
            paper.Items = Items(count);
            prepare?.Invoke(paper.Items[0]);
            History(window, "_undoStack").Clear();
            History(window, "_redoStack").Clear();
            window.RefreshTodoRowsForExternalChange();
            await Idle();
            await Focus(window, Editors(window)["row-0"]);
            RequireNoAppendArea(window);
        }
    }

    private static List<PaperItem> Items(int count) => Enumerable.Range(0, count)
        .Select(index => new PaperItem { Id = "row-" + index, Order = index, Text = "Todo history row " + index })
        .ToList();

    private static async Task Focus(PaperWindow window, TodoTextBox editor)
    {
        LifecycleInput.Focus(window, editor);
        await Idle();
        Require(editor.IsKeyboardFocused, "fixture could not give the live Todo editor keyboard focus");
    }

    private static async Task SendKey(PaperWindow window, Key key, bool expectPaperHistory = true, bool control = true)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = false;
        var handledAtWindow = false;
        KeyEventHandler down = (_, e) =>
        {
            if (e.Key != key) return;
            observed = true;
            handledAtWindow = e.Handled;
        };
        KeyEventHandler up = (_, e) => { if (e.Key == key && observed) completed.TrySetResult(true); };
        window.AddHandler(Keyboard.PreviewKeyDownEvent, down, handledEventsToo: true);
        window.AddHandler(Keyboard.PreviewKeyUpEvent, up, handledEventsToo: true);
        try
        {
            var virtualKey = (ushort)KeyInterop.VirtualKeyFromKey(key);
            LifecycleInput.KeyChord(control ? [0x11, virtualKey] : [virtualKey]);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Idle();
            Require(observed, "native keyboard input was not received by the PaperWindow");
            if (expectPaperHistory)
                Require(handledAtWindow, "native Ctrl+Z/Y did not enter the paper-level history handler");
        }
        finally
        {
            window.RemoveHandler(Keyboard.PreviewKeyDownEvent, down);
            window.RemoveHandler(Keyboard.PreviewKeyUpEvent, up);
        }
    }

    private sealed record Snapshot(Dictionary<string, Border> Rows, Dictionary<string, TodoTextBox> Editors,
        Dictionary<string, PaperItem> Items);

    private static Snapshot Capture(PaperWindow window, PaperData paper) =>
        new(Rows(window), new Dictionary<string, TodoTextBox>(Editors(window)), paper.Items.ToDictionary(item => item.Id));

    private static void RequireRetained(Snapshot before, PaperWindow window, PaperData paper, string[] changed, string operation,
        bool appendArea = false)
    {
        var rows = Rows(window);
        var editors = Editors(window);
        foreach (var item in paper.Items)
        {
            if (!before.Items.TryGetValue(item.Id, out var old)) continue;
            var expected = !changed.Contains(item.Id);
            Require(ReferenceEquals(old, item) == expected &&
                    ReferenceEquals(before.Rows[item.Id], rows[item.Id]) == expected &&
                    ReferenceEquals(before.Editors[item.Id], editors[item.Id]) == expected,
                operation + " did not retain/rebuild the expected model and controls for " + item.Id);
            Require(editors[item.Id].Text == item.Text && editors[item.Id].IsDone == item.Done &&
                    CheckBox(window, item.Id).IsChecked == item.Done,
                operation + " left stale text or completion presentation for " + item.Id);
        }
        Require(paper.Items.Select(item => item.Id).SequenceEqual(((List<Border>)Field(window, "_todoRows")).Select(row => (string)row.Tag)),
            operation + " did not synchronize visual row order");
        Require(paper.Items.Select(item => item.Id).SequenceEqual(((StackPanel)Field(window, "_todoPanel")).Children
                .OfType<Border>().Where(row => row.Tag is string).Select(row => (string)row.Tag)),
            operation + " did not synchronize the actual panel child order");
        Require(paper.Items.Select(item => item.Order).SequenceEqual(Enumerable.Range(0, paper.Items.Count)),
            operation + " did not normalize persisted order");
        if (appendArea)
        {
            var panel = (StackPanel)Field(window, "_todoPanel");
            Require(panel.Children.Count == rows.Count + 1 &&
                    ReferenceEquals(panel.Children[panel.Children.Count - 1], Field(window, "_appendArea")),
                operation + " did not preserve the bottom append area");
        }
        else RequireNoAppendArea(window);
    }

    private static void RequireItem(PaperItem actual, PaperItem expected, string operation) => Require(
        actual.Id == expected.Id && actual.Text == expected.Text && actual.Done == expected.Done && actual.Order == expected.Order &&
        actual.LinkedPaperId == expected.LinkedPaperId && actual.LinkedPath == expected.LinkedPath &&
        actual.LinkedPathIsDirectory == expected.LinkedPathIsDirectory && actual.ReminderAt == expected.ReminderAt &&
        actual.ReminderAt?.Offset == expected.ReminderAt?.Offset && actual.ReminderTriggered == expected.ReminderTriggered, operation);

    private static void RequireHistoryIsolation(PaperWindow window, PaperData paper) => Require(
        History(window, "_undoStack").Concat(History(window, "_redoStack")).SelectMany(items => items)
            .All(saved => paper.Items.All(current => !ReferenceEquals(saved, current))),
        "a retained live model became shared with a saved history snapshot");

    private static void RequireSelection(TodoTextBox editor, (int Start, int Length, int Caret) expected, string operation) => Require(
        (editor.SelectionStart, editor.SelectionLength, editor.CaretIndex) == expected, operation + " changed the retained text selection");

    private static void RequireNoAppendArea(PaperWindow window) => Require(
        typeof(PaperWindow).GetField("_appendArea", Private)!.GetValue(window) == null &&
        ((StackPanel)Field(window, "_todoPanel")).Children.Count == Rows(window).Count,
        "history replay restored a disabled bottom append area");

    private sealed class CountingStackPanel : StackPanel
    {
        internal List<DependencyObject> Added { get; } = [];
        internal List<DependencyObject> Removed { get; } = [];

        protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
        {
            base.OnVisualChildrenChanged(visualAdded, visualRemoved);
            if (visualAdded != null) Added.Add(visualAdded);
            if (visualRemoved != null) Removed.Add(visualRemoved);
        }

        internal void ResetCounts()
        {
            Added.Clear();
            Removed.Clear();
        }
    }

    private static CountingStackPanel InstallCountingPanel(PaperWindow window)
    {
        var original = (StackPanel)Field(window, "_todoPanel");
        DependencyObject? parent = VisualTreeHelper.GetParent(original);
        while (parent != null && parent is not ScrollViewer)
            parent = VisualTreeHelper.GetParent(parent);
        Require(parent is ScrollViewer host && ReferenceEquals(host.Content, original),
            "fixture could not find the Todo panel's scroll host");
        var scroll = (ScrollViewer)parent!;
        var panel = new CountingStackPanel { Margin = original.Margin };
        SetField(window, "_activeOriginalItemId", null);
        SetField(window, "_activeOriginalText", null);
        scroll.Content = null;
        var children = original.Children.Cast<UIElement>().ToArray();
        original.Children.Clear();
        foreach (var child in children) panel.Children.Add(child);
        SetField(window, "_todoPanel", panel);
        scroll.Content = panel;
        return panel;
    }

    private static void RequireVisualChanges(CountingStackPanel panel, int added, int removed, string operation) => Require(
        panel.Added.Count == added && panel.Removed.Count == removed,
        $"{operation} changed too many/few actual WPF visual children: added {panel.Added.Count}, removed {panel.Removed.Count}; expected {added}/{removed}");

    private static void ReconcileRows(PaperWindow window, IEnumerable<string>? rebuildIds = null)
    {
        var method = typeof(PaperWindow).GetMethod("ReconcileTodoRows", Private)!;
        var placement = Enum.Parse(method.GetParameters()[2].ParameterType, "End");
        method.Invoke(window, [rebuildIds, null, placement]);
    }

    private static CheckBox CheckBox(PaperWindow window, string id) => ((Grid)Rows(window)[id].Child).Children.OfType<CheckBox>().Single();
    private static Dictionary<string, TodoTextBox> Editors(PaperWindow window) => (Dictionary<string, TodoTextBox>)Field(window, "_todoEditors");
    private static Dictionary<string, Border> Rows(PaperWindow window) => ((List<Border>)Field(window, "_todoRows")).ToDictionary(row => (string)row.Tag);
    private static List<List<PaperItem>> History(PaperWindow window, string name) => (List<List<PaperItem>>)Field(window, name);
    private static object Field(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static void Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).Task;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
