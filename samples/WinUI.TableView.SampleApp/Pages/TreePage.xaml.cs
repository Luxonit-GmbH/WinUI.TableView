using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace WinUI.TableView.SampleApp.Pages;

public sealed partial class TreePage : Page
{
    public TreePage()
    {
        InitializeComponent();

        // Nested collections in the app's natural shape; the control wraps them in a TreeTableViewSource itself.
        var roots = new ObservableCollection<ITableViewTreeItem>();

        for (var i = 1; i <= 50000; i++)
        {
            roots.Add(new TreeItemModel($"Portfolio {i:D2}", "Portfolio", depth: 0, childCount: Random.Shared.Next(3, 100)));
        }

        treeView.TreeItemsSource = roots; // one line: adapter created, UseCollectionView off, bound

        // Collapse needs no wiring at all (automatic). Expand only needs a handler because children are fetched
        // lazily from a "backend" here — a fully pre-loaded tree would need no handlers whatsoever.
        treeView.ExpandRequested += OnExpandRequested;

        indentSlider.Value = treeColumn.IndentWidth;
        indentSlider.ValueChanged += (_, e) => treeColumn.IndentWidth = e.NewValue;
    }

    private async void OnExpandRequested(object? sender, TreeTableViewExpandCollapseEventArgs e)
    {
        var node = (TreeItemModel)e.Item;

        // The app's backend model: the child COUNT is known upfront, the children are fetched on first expand.
        if (!node.ChildrenLoaded)
        {
            node.IsLoading = true; // chevron shows a progress ring; repeated requests are ignored meanwhile

            if (slowBackendToggle.IsOn)
            {
                await Task.Delay(800);
            }

            node.LoadChildren();
            node.IsLoading = false;

            // Complete the deferred expansion now that the children exist (the automatic expand ran before the
            // fetch finished and could only mark the item expanded).
            treeView.TreeSource!.Expand(node);
        }
    }
}

/// <summary>
/// Demo tree item: three levels (Portfolio - Book - Order), children created lazily on first expand.
/// </summary>
public sealed partial class TreeItemModel : ITableViewTreeItem
{
    private bool _isExpanded;
    private bool _isLoading;

    public TreeItemModel(string name, string kind, int depth, int childCount)
    {
        Name = name;
        Kind = kind;
        Depth = depth;
        ChildCount = childCount;
        Value = Math.Round(Random.Shared.NextDouble() * 1_000_000, 2);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public string Kind { get; }
    public double Value { get; }
    public int ChildCount { get; }
    public int Depth { get; }

    public ObservableCollection<ITableViewTreeItem>? Children { get; private set; }
    public System.Collections.IEnumerable? ChildrenSource => Children;

    public bool ChildrenLoaded => Children is not null;
    public bool HasChildren => ChildCount > 0;

    // Orders are terminal: no expand/collapse event is ever raised for them, no matter the gesture.
    public bool IsFinalItem => Kind == "Order";

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value, nameof(IsExpanded));
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => Set(ref _isLoading, value, nameof(IsLoading));
    }

    public void LoadChildren()
    {
        if (Children is not null)
        {
            return;
        }

        Children = [];

        for (var i = 1; i <= ChildCount; i++)
        {
            Children.Add(Depth == 0
                ? new TreeItemModel($"{Name} / Book {i}", "Book", depth: 1, childCount: Random.Shared.Next(2, 12))
                : new TreeItemModel($"Order {Random.Shared.Next(100_000, 999_999)}", "Order", depth: Depth + 1, childCount: 0));
        }
    }

    private void Set(ref bool field, bool value, string name)
    {
        if (field != value)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
