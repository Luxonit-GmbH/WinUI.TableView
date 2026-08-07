using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.ComponentModel;

namespace WinUI.TableView;

/// <summary>
/// One banner in the second header level: the visual for a <see cref="TableViewColumnGroup"/> spanning a run of
/// columns, with its own chevron to collapse and expand it. Created by <see cref="TableViewHeaderRow"/>; not
/// intended to be used directly.
/// </summary>
public partial class TableViewColumnGroupHeader : ContentControl
{
    private const double ExpandedAngle = 0d;    // ChevronLeft as authored — click to collapse
    private const double CollapsedAngle = 180d; // rotated to point the other way — click to expand

    private static readonly TimeSpan ChevronDuration = TimeSpan.FromMilliseconds(150);

    private FontIcon? _chevron;
    private RotateTransform? _chevronRotation;
    private Storyboard? _chevronStoryboard;
    private TableView? _tableView;
    private bool? _shownAsCollapsed;

    /// <summary>
    /// Initializes a new instance of the TableViewColumnGroupHeader class.
    /// </summary>
    public TableViewColumnGroupHeader()
    {
        DefaultStyleKey = typeof(TableViewColumnGroupHeader);
    }

    /// <summary>
    /// Gets the group this banner represents, or <see langword="null"/> for the filler above ungrouped columns.
    /// </summary>
    public TableViewColumnGroup? Group { get; private set; }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _chevron = GetTemplateChild("CollapseChevron") as FontIcon;
        _chevronRotation = GetTemplateChild("ChevronRotation") as RotateTransform;
        _shownAsCollapsed = null; // the fresh template starts unrotated; settle it without animating

        Apply();
    }

    /// <summary>
    /// Points the banner at a group and the grid that owns it. Always re-subscribes, so a recycled banner never
    /// keeps listening to the group it used to show.
    /// </summary>
    internal void Attach(TableViewColumnGroup? group, TableView? tableView)
    {
        if (Group is not null)
        {
            Group.PropertyChanged -= OnGroupPropertyChanged;
        }

        Group = group;
        _tableView = tableView;
        _shownAsCollapsed = null; // a different group: adopt its state, do not animate into it

        if (Group is not null)
        {
            Group.PropertyChanged += OnGroupPropertyChanged;
        }

        Apply();
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e) => Apply();

    private void Apply()
    {
        Content = Group?.Header ?? Group?.Name;
        ContentTemplate = Group?.HeaderTemplate;

        // The filler above ungrouped columns is inert: no chevron, no clicks.
        var collapsible = Group is { IsCollapsible: true };
        IsHitTestVisible = collapsible;

        if (_chevron is not null)
        {
            _chevron.Visibility = collapsible ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateChevron(Group?.IsCollapsed is true);
    }

    /// <summary>
    /// Turns the chevron to match the group's state, animating only when the state actually changed — a first
    /// render or a re-attach snaps, so banners do not spin on load or while scrolling.
    /// </summary>
    private void UpdateChevron(bool collapsed)
    {
        if (_chevronRotation is null || _shownAsCollapsed == collapsed)
        {
            return;
        }

        var animate = _shownAsCollapsed is not null;
        _shownAsCollapsed = collapsed;

        var target = collapsed ? CollapsedAngle : ExpandedAngle;

        _chevronStoryboard?.Stop();

        if (!animate)
        {
            _chevronRotation.Angle = target;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = target,
            Duration = ChevronDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard.SetTarget(animation, _chevronRotation);
        Storyboard.SetTargetProperty(animation, nameof(RotateTransform.Angle));

        _chevronStoryboard = new Storyboard();
        _chevronStoryboard.Children.Add(animation);
        _chevronStoryboard.Begin();
    }

    /// <inheritdoc/>
    protected override void OnTapped(TappedRoutedEventArgs e)
    {
        base.OnTapped(e);

        if (Group is { IsCollapsible: true } group && _tableView is not null)
        {
            _tableView.SetColumnGroupCollapsed(group, !group.IsCollapsed);
            e.Handled = true;
        }
    }
}
