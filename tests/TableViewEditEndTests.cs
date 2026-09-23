using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WinUI.TableView.Extensions;

namespace WinUI.TableView.Tests;

/// <summary>
/// Ending a cell edit: from code through <see cref="TableView.TryEndCurrentCellEdit"/>, and by keyboard focus
/// leaving the cell.
/// </summary>
[TestClass]
public class TableViewEditEndTests
{
    // ---------------------------------------------------------------------------------------------------------
    // TryEndCurrentCellEdit
    // ---------------------------------------------------------------------------------------------------------

    [UITestMethod]
    public async Task TryEndCurrentCellEdit_Cancel_DiscardsTheEditorsValue()
    {
        var (tableView, _) = await LoadAsync();
        var events = Record(tableView);
        var (cell, editor) = await BeginEditAsync<TextBox>(tableView);
        editor.Text = "changed";

        Assert.IsTrue(tableView.TryEndCurrentCellEdit(TableViewEditAction.Cancel));

        Assert.IsFalse(tableView.IsEditing);
        Assert.AreEqual("Item 0", ItemAt(tableView, 0).Name, "cancel must not write the editor's value");
        tableView.UpdateLayout(); // the display element's binding reads the item on its first layout
        await SettleAsync();
        Assert.AreEqual("Item 0", (cell.Content as TextBlock)?.Text, "the cell shows the item's value again");
        CollectionAssert.AreEqual(new[] { "Ending:Cancel", "Ended:Cancel" }, events);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task TryEndCurrentCellEdit_Commit_WritesTheEditorsValue()
    {
        var (tableView, _) = await LoadAsync();
        var events = Record(tableView);
        var (cell, editor) = await BeginEditAsync<TextBox>(tableView);
        editor.Text = "changed";

        Assert.IsTrue(tableView.TryEndCurrentCellEdit(TableViewEditAction.Commit));

        Assert.IsFalse(tableView.IsEditing);
        Assert.AreEqual("changed", ItemAt(tableView, 0).Name);
        tableView.UpdateLayout(); // the display element's binding reads the item on its first layout
        await SettleAsync();
        Assert.AreEqual("changed", (cell.Content as TextBlock)?.Text);
        CollectionAssert.AreEqual(new[] { "Ending:Commit", "Ended:Commit" }, events);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task TryEndCurrentCellEdit_ReturnsTrue_WhenNothingIsBeingEdited()
    {
        var (tableView, _) = await LoadAsync();
        var events = Record(tableView);

        Assert.IsTrue(tableView.TryEndCurrentCellEdit(TableViewEditAction.Cancel));
        Assert.AreEqual(0, events.Count, "no edit, no events");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task TryEndCurrentCellEdit_ReturnsFalse_AndKeepsEditing_WhenAHandlerRefuses()
    {
        var (tableView, _) = await LoadAsync();
        var (cell, editor) = await BeginEditAsync<TextBox>(tableView);
        editor.Text = "changed";

        void Refuse(object? sender, TableViewCellEditEndingEventArgs e) => e.Cancel = true;
        tableView.CellEditEnding += Refuse;

        Assert.IsFalse(tableView.TryEndCurrentCellEdit(TableViewEditAction.Cancel));
        Assert.IsTrue(tableView.IsEditing, "a refused ending keeps the grid in edit mode");
        Assert.AreSame(editor, cell.Content, "the editor stays in the cell");
        Assert.AreEqual("changed", editor.Text, "with what the user typed");

        tableView.CellEditEnding -= Refuse;

        Assert.IsTrue(tableView.TryEndCurrentCellEdit(TableViewEditAction.Cancel));
        Assert.IsFalse(tableView.IsEditing);
        Assert.AreEqual("Item 0", ItemAt(tableView, 0).Name);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task TryEndCurrentCellEdit_LeavesEditMode_WhenThereIsNoCellLeftToEnd()
    {
        var (tableView, _) = await LoadAsync();
        var events = Record(tableView);
        tableView.CurrentCellSlot = null;
        tableView.SetIsEditing(true); // an edit whose cell is gone: nothing to commit or cancel against

        Assert.IsTrue(tableView.TryEndCurrentCellEdit(TableViewEditAction.Commit));
        Assert.IsFalse(tableView.IsEditing, "the grid must not stay stuck in edit mode");
        Assert.AreEqual(0, events.Count, "there is no cell to raise the events for");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task TryEndCurrentCellEdit_MovesFocusFromTheEditorToTheCell()
    {
        var (tableView, _) = await LoadAsync();
        var (cell, editor) = await BeginEditAsync<TextBox>(tableView);
        RequireFocus(tableView, editor);

        Assert.IsTrue(tableView.TryEndCurrentCellEdit(TableViewEditAction.Cancel));

        Assert.AreSame(cell, FocusedElement(tableView), "focus stays on the cell rather than jumping to the next element");

        await UnloadAsync(tableView);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Focus loss
    // ---------------------------------------------------------------------------------------------------------

    [UITestMethod]
    public async Task FocusLeavingTheCell_CommitsTheEdit()
    {
        var (tableView, outside) = await LoadAsync();
        var events = Record(tableView);
        var (_, editor) = await BeginEditAsync<TextBox>(tableView);
        RequireFocus(tableView, editor);
        editor.Text = "changed";

        outside.Focus(FocusState.Programmatic);
        await WaitUntilAsync(() => !tableView.IsEditing);

        Assert.IsFalse(tableView.IsEditing, "focus moving to another control must end the edit");
        Assert.AreEqual("changed", ItemAt(tableView, 0).Name, "and commit it");
        CollectionAssert.AreEqual(new[] { "Ending:Commit", "Ended:Commit" }, events);
        Assert.AreSame(outside, FocusedElement(tableView), "focus stays where the user put it");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task FocusMovingWithinTheCell_KeepsEditing()
    {
        var (tableView, _) = await LoadAsync();
        var (cell, editor) = await BeginEditAsync<TextBox>(tableView);
        RequireFocus(tableView, editor);

        cell.Focus(FocusState.Programmatic);
        await SettleAsync();

        Assert.IsTrue(tableView.IsEditing, "focus still inside the cell is not focus loss");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task FocusMovingIntoAPopup_KeepsEditing_UntilItLeavesTheEditorAgain()
    {
        var (tableView, outside) = await LoadAsync();
        var (_, editor) = await BeginEditAsync<TextBox>(tableView);
        RequireFocus(tableView, editor);
        editor.Text = "changed";

        // Stands in for a picker's flyout or any other popup: focus goes there and comes back to the editor.
        var inPopup = new Button { Content = "in popup" };
        var popup = new Popup { XamlRoot = tableView.XamlRoot, Child = inPopup };
        popup.IsOpen = true;
        await SettleAsync();
        inPopup.Focus(FocusState.Programmatic);
        await SettleAsync();

        Assert.IsTrue(tableView.IsEditing, "focus in a popup does not end the edit");

        popup.IsOpen = false;
        editor.Focus(FocusState.Programmatic);
        await SettleAsync();
        outside.Focus(FocusState.Programmatic);
        await WaitUntilAsync(() => !tableView.IsEditing);

        Assert.IsFalse(tableView.IsEditing, "leaving the editor for another control afterwards still commits");
        Assert.AreEqual("changed", ItemAt(tableView, 0).Name);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task FocusLeavingTheCell_KeepsEditing_WhenAHandlerRefuses_AndAsksOnce()
    {
        var (tableView, outside) = await LoadAsync();
        var (_, editor) = await BeginEditAsync<TextBox>(tableView);
        RequireFocus(tableView, editor);
        editor.Text = "changed";

        var asked = 0;
        void Refuse(object? sender, TableViewCellEditEndingEventArgs e) { asked++; e.Cancel = true; }
        tableView.CellEditEnding += Refuse;

        outside.Focus(FocusState.Programmatic);
        await WaitUntilAsync(() => asked > 0);
        var another = new Button();
        ((Panel)outside.Parent).Children.Add(another);
        await SettleAsync();
        another.Focus(FocusState.Programmatic); // focus moving on elsewhere is not focus leaving the editor
        await SettleAsync();

        Assert.IsTrue(tableView.IsEditing, "a refused commit keeps the grid in edit mode");
        Assert.AreEqual(1, asked, "the handler is asked when focus leaves the editor, not on every move after");
        Assert.AreEqual("Item 0", ItemAt(tableView, 0).Name);

        tableView.CellEditEnding -= Refuse;
        editor.Focus(FocusState.Programmatic);
        await SettleAsync();
        outside.Focus(FocusState.Programmatic);
        await WaitUntilAsync(() => !tableView.IsEditing);

        Assert.IsFalse(tableView.IsEditing, "leaving the editor again asks again");
        Assert.AreEqual("changed", ItemAt(tableView, 0).Name);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task FocusLeavingTheCell_Commits_WhenTheGridItselfIsInAPopup()
    {
        // A grid hosted in a flyout or a dialog lives in a popup; a button in that same popup is still outside the cell.
        var tableView = CreateTableView();
        var inSamePopup = new Button { Content = "save" };
        var panel = new StackPanel { Width = 600, Height = 450 };
        panel.Children.Add(tableView);
        panel.Children.Add(inSamePopup);

        var window = UnitTestApp.Current.MainWindow;
        var placeholder = new Grid();
        await window.LoadTestContentAsync(placeholder);

        var loaded = new TaskCompletionSource();
        tableView.Loaded += (_, _) => loaded.TrySetResult();
        var popup = new Popup { XamlRoot = placeholder.XamlRoot, Child = panel };
        popup.IsOpen = true;
        await loaded.Task;
        await SettleAsync();

        var (_, editor) = await BeginEditAsync<TextBox>(tableView);
        RequireFocus(tableView, editor);
        editor.Text = "changed";

        inSamePopup.Focus(FocusState.Programmatic);
        await WaitUntilAsync(() => !tableView.IsEditing);

        Assert.IsFalse(tableView.IsEditing, "the popup hosting the grid is not a popup the editor opened");
        Assert.AreEqual("changed", ItemAt(tableView, 0).Name);

        popup.IsOpen = false;
        await window.UnloadTestContentAsync(placeholder);
    }

    [UITestMethod]
    public async Task FocusMovingIntoTheEditorsOwnDropDown_KeepsEditing()
    {
        var (tableView, _) = await LoadAsync(new TableViewComboBoxColumn
        {
            Header = "Name",
            Width = new GridLength(200),
            ItemsSource = new[] { "Item 0", "A", "B" },
            Binding = new Binding { Path = new PropertyPath(nameof(Item.Name)) },
        });
        var (_, comboBox) = await BeginEditAsync<ComboBox>(tableView);
        RequireFocus(tableView, comboBox);

        comboBox.IsDropDownOpen = true;
        await WaitUntilAsync(() => FocusedElement(tableView) is ComboBoxItem);

        if (FocusedElement(tableView) is not ComboBoxItem)
        {
            Assert.Inconclusive($"the drop-down did not take focus (focused: {FocusedElement(tableView)?.GetType().Name ?? "nothing"})");
        }

        await SettleAsync();

        Assert.IsTrue(tableView.IsEditing, "an item in the editor's own drop-down is still inside the cell");

        comboBox.IsDropDownOpen = false;
        await SettleAsync();
        await UnloadAsync(tableView);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private static TableView CreateTableView(TableViewColumn? column = null)
    {
        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            RowHeight = 32,
            Height = 300,
            ItemsSource = new ObservableCollection<Item>(Enumerable.Range(0, 50).Select(i => new Item { Name = $"Item {i}" })),
        };

        tableView.Columns.Add(column ?? new TableViewTextColumn
        {
            Header = "Name",
            Width = new GridLength(200),
            Binding = new Binding { Path = new PropertyPath(nameof(Item.Name)) },
        });

        return tableView;
    }

    private static async Task<(TableView TableView, Button Outside)> LoadAsync(TableViewColumn? column = null)
    {
        var tableView = CreateTableView(column);
        var outside = new Button { Content = "outside" };
        var panel = new StackPanel { Width = 600 };
        panel.Children.Add(outside);
        panel.Children.Add(tableView);

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(panel);
        tableView.UpdateLayout();

        return (tableView, outside);
    }

    private static async Task UnloadAsync(TableView tableView)
        => await UnitTestApp.Current.MainWindow.UnloadTestContentAsync((FrameworkElement)tableView.Parent);

    /// <summary>
    /// Makes row 0's cell current and puts it into edit mode, then waits for the editor to load: that is when it
    /// takes focus and records the value it started from.
    /// </summary>
    private static async Task<(TableViewCell Cell, TEditor Editor)> BeginEditAsync<TEditor>(TableView tableView) where TEditor : Control
    {
        var slot = new TableViewCellSlot(0, 0);
        tableView.CurrentCellSlot = slot;
        await SettleAsync();

        var cell = tableView.GetCellFromSlot(slot);
        Assert.IsNotNull(cell, "row 0's cell must be realized");
        Assert.IsTrue(cell!.BeginCellEditing(new RoutedEventArgs()), "editing must begin");

        await WaitUntilAsync(() => cell.Content is TEditor { IsLoaded: true });
        await SettleAsync();

        Assert.IsTrue(tableView.IsEditing);
        return (cell, (TEditor)cell.Content);
    }

    /// <summary>
    /// The focus tests need the editor to hold keyboard focus, which it takes itself once loaded. If the test host's
    /// window cannot hold focus at all there is nothing to test, which is not a failure of the grid.
    /// </summary>
    private static void RequireFocus(TableView tableView, Control editor)
    {
        if (!ReferenceEquals(FocusedElement(tableView), editor))
        {
            Assert.Inconclusive($"the editor did not take focus (focused: {FocusedElement(tableView)?.GetType().Name ?? "nothing"})");
        }
    }

    private static object? FocusedElement(TableView tableView) => FocusManager.GetFocusedElement(tableView.XamlRoot);

    private static List<string> Record(TableView tableView)
    {
        var events = new List<string>();
        tableView.CellEditEnding += (_, e) => events.Add($"Ending:{e.EditAction}");
        tableView.CellEditEnded += (_, e) => events.Add($"Ended:{e.EditAction}");
        return events;
    }

    private static Item ItemAt(TableView tableView, int index) => (Item)tableView.Items[index];

    private static async Task SettleAsync()
    {
        await Task.Delay(60);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var waited = 0; !condition() && waited < 2000; waited += 20)
        {
            await Task.Delay(20);
        }
    }

    private sealed class Item
    {
        public string Name { get; set; } = string.Empty;
    }
}
