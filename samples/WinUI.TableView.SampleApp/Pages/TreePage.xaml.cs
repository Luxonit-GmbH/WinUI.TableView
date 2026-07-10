using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace WinUI.TableView.SampleApp.Pages;

public sealed partial class TreePage : Page
{
    private readonly TreeTableViewSource _source;

    public TreePage()
    {
        InitializeComponent();

        // Nested collections in the app's natural shape; TreeTableViewSource flattens them for the grid.
        var roots = new ObservableCollection<ITableViewTreeItem>();

        for (var i = 1; i <= 25; i++)
        {
            roots.Add(new TreeItemModel($"Portfolio {i:D2}", "Portfolio", depth: 0, childCount: Random.Shared.Next(3, 8)));
        }

        _source = new TreeTableViewSource(roots);
        treeView.ItemsSource = _source;

        treeView.ExpandRequested += OnExpandRequested;
        treeView.CollapseRequested += (_, e) => _source.Collapse((TreeItemModel)e.Item);

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
        }

        _source.Expand(node);
    }
}

/// <summary>
/// Demo tree item: three levels (Portfolio - Book - Order), children created lazily on first expand.
/// </summary>
public sealed partial class TreeItemModel : ITableViewTreeNode
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
    public IEnumerable<ITableViewTreeItem>? ChildrenSource => Children;

    public bool ChildrenLoaded => Children is not null;
    public bool HasChildren => ChildCount > 0;

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
