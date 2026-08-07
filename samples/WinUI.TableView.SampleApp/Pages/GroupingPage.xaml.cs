using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace WinUI.TableView.SampleApp.Pages;

public sealed partial class GroupingPage : Page
{
    private static readonly string[] Departments = ["Engineering", "Finance", "HR", "Marketing", "Sales"];
    private static readonly string[] Currencies = ["EUR", "USD", "GBP", "JPY"];

    private readonly List<TableViewColumnGroup> _columnGroups = [];

    public GroupingPage()
    {
        InitializeComponent();

        // Keep the definitions so the column-grouping toggle can put them back.
        _columnGroups.AddRange(grid.ColumnGroups);

        ApplySource();

        groupByCombo.SelectionChanged += (_, _) => ApplyGroupBy();
        groupSortCombo.SelectionChanged += (_, _) => ApplyGroupSort();
        showHeadersToggle.Toggled += (_, _) => grid.ShowGroupHeaders = showHeadersToggle.IsOn;
        showCountToggle.Toggled += (_, _) => grid.ShowGroupItemCount = showCountToggle.IsOn;
        columnGroupingToggle.Toggled += (_, _) => ApplyColumnGrouping(columnGroupingToggle.IsOn);
        collapseGroupsToggle.Toggled += (_, _) => ApplyCollapse(collapseGroupsToggle.IsOn);
        expandAllButton.Click += (_, _) => grid.SetAllGroupsExpanded(true);
        collapseAllButton.Click += (_, _) => grid.SetAllGroupsExpanded(false);
        treesInGroupsToggle.Toggled += (_, _) => ApplySource();

        ApplyGroupBy();
    }

    /// <summary>
    /// Swaps between plain rows and rows that are themselves tree items. Either way the collection handed to the
    /// grid is FLAT — grouping is the grid's job, and a group's members being expandable is orthogonal to it.
    /// </summary>
    private void ApplySource()
    {
        var trades = BuildTrades().ToList();

        grid.ItemsSource = treesInGroupsToggle?.IsOn is true
            ? new ObservableCollection<object>(trades.Select(trade => new TradeNode(trade)))
            : new ObservableCollection<object>(trades);
    }

    private void ApplyGroupBy()
        => grid.GroupByPath = (groupByCombo.SelectedItem as ComboBoxItem)?.Tag as string;

    private void ApplyGroupSort()
        => grid.GroupSortDirection = (groupSortCombo.SelectedItem as ComboBoxItem)?.Tag as string switch
        {
            "Descending" => SortDirection.Descending,
            "None" => null,
            _ => SortDirection.Ascending,
        };

    /// <summary>
    /// Turning column grouping off removes the definitions; the columns keep their GroupName, which then matches
    /// nothing. The banner row measures to zero and the header looks untouched.
    /// </summary>
    private void ApplyColumnGrouping(bool on)
    {
        if (!on)
        {
            ApplyCollapse(false); // never leave columns hidden behind a banner that is about to disappear
            collapseGroupsToggle.IsOn = false;
            grid.ColumnGroups.Clear();
            return;
        }

        foreach (var group in _columnGroups)
        {
            grid.ColumnGroups.Add(group);
        }
    }

    private void ApplyCollapse(bool collapse)
    {
        foreach (var group in _columnGroups.Where(group => group.Name is "Prices" or "Risk"))
        {
            if (grid.ColumnGroups.Contains(group))
            {
                grid.SetColumnGroupCollapsed(group, collapse);
            }
        }
    }

    private static IEnumerable<TradeRow> BuildTrades()
    {
        var random = new Random(0x5EED); // fixed seed: the demo looks the same every run

        for (var i = 1; i <= 200; i++)
        {
            var currency = Currencies[i % Currencies.Length];
            var mid = 50 + random.NextDouble() * 100;

            yield return new TradeRow
            {
                Name = $"{currency} trade {i:D3}",
                Currency = currency,
                Department = Departments[i % Departments.Length],
                Bid = Math.Round(mid - 0.25, 2),
                Mid = Math.Round(mid, 2),
                Ask = Math.Round(mid + 0.25, 2),
                Delta = Math.Round(random.NextDouble() * 2 - 1, 3),
                Vega = Math.Round(random.NextDouble() * 10, 2),
            };
        }
    }
}

/// <summary>
/// A trade that is ALSO a tree node: its fills hang underneath it. Grouping does not know or care — a group's
/// members are just rows, and a row being expandable is its own business. That composition is the reason
/// grouping is built on the tree adapter rather than beside it.
/// </summary>
public sealed partial class TradeNode : ITableViewTreeItem
{
    private bool _isExpanded;

    public TradeNode(TradeRow trade)
    {
        Trade = trade;
        Fills = [.. Enumerable.Range(1, 3).Select(i => new TradeRow
        {
            Name = $"    fill {i}",
            Currency = trade.Currency,
            Department = trade.Department,
            Bid = trade.Bid,
            Mid = trade.Mid,
            Ask = trade.Ask,
            Delta = Math.Round(trade.Delta / 3, 3),
            Vega = Math.Round(trade.Vega / 3, 2),
        })];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TradeRow Trade { get; }
    public ObservableCollection<TradeRow> Fills { get; }

    // The grid binds these by name, so a node reads exactly like the trade it wraps.
    public string Name => Trade.Name;
    public string Currency => Trade.Currency;
    public string Department => Trade.Department;
    public double Bid => Trade.Bid;
    public double Mid => Trade.Mid;
    public double Ask => Trade.Ask;
    public double Delta => Trade.Delta;
    public double Vega => Trade.Vega;

    public int Depth => 1; // one level in from the group header
    public IEnumerable? ChildrenSource => Fills;
    public bool HasChildren => true;
    public bool IsFinalItem => false;
    public bool IsLoading => false;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }
    }
}

/// <summary>
/// A plain row model — nothing about it knows it will be grouped.
/// </summary>
public sealed class TradeRow
{
    public string Name { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public double Bid { get; set; }
    public double Mid { get; set; }
    public double Ask { get; set; }
    public double Delta { get; set; }
    public double Vega { get; set; }
}
