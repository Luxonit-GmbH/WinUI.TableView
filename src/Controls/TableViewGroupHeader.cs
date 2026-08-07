using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.ComponentModel;
using WinUI.TableView.Extensions;

namespace WinUI.TableView;

/// <summary>
/// The default visual for a <see cref="TableViewGroup"/> header row: a chevron, the group's title, and its item
/// count. Replaceable wholesale via <see cref="TableView.GroupHeaderTemplate"/>.
/// </summary>
public partial class TableViewGroupHeader : Control
{
    private static readonly TimeSpan ChevronDuration = TimeSpan.FromMilliseconds(150);

    private RotateTransform? _chevronRotation;
    private Storyboard? _chevronStoryboard;
    private bool? _shownAsExpanded;

    /// <summary>
    /// Initializes a new instance of the TableViewGroupHeader class.
    /// </summary>
    public TableViewGroupHeader()
    {
        DefaultStyleKey = typeof(TableViewGroupHeader);
        DataContextChanged += (_, _) => Attach(DataContext as TableViewGroup);
    }

    /// <summary>
    /// Identifies the <see cref="Group"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty GroupProperty = DependencyProperty.Register(
        nameof(Group), typeof(TableViewGroup), typeof(TableViewGroupHeader), new PropertyMetadata(null));

    /// <summary>
    /// Gets the group this header represents.
    /// </summary>
    public TableViewGroup? Group
    {
        get => (TableViewGroup?)GetValue(GroupProperty);
        private set => SetValue(GroupProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="ShowItemCount"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty ShowItemCountProperty = DependencyProperty.Register(
        nameof(ShowItemCount), typeof(bool), typeof(TableViewGroupHeader), new PropertyMetadata(true));

    /// <summary>
    /// Gets or sets whether the member count is shown beside the title.
    /// </summary>
    public bool ShowItemCount
    {
        get => (bool)GetValue(ShowItemCountProperty);
        set => SetValue(ShowItemCountProperty, value);
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _chevronRotation = GetTemplateChild("ChevronRotation") as RotateTransform;
        _shownAsExpanded = null; // a fresh template adopts the angle instead of turning into it

        UpdateChevron();
    }

    private void Attach(TableViewGroup? group)
    {
        if (Group is not null)
        {
            Group.PropertyChanged -= OnGroupPropertyChanged;
        }

        Group = group;
        _shownAsExpanded = null; // recycled onto a different group: adopt, do not animate

        if (Group is not null)
        {
            Group.PropertyChanged += OnGroupPropertyChanged;
        }

        UpdateChevron();
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateChevron();

    /// <summary>
    /// Turns the chevron to match the group's state, animating only a real change so headers do not spin on load
    /// or as scrolling recycles them.
    /// </summary>
    private void UpdateChevron()
    {
        var expanded = Group?.IsExpanded ?? true;

        if (_chevronRotation is null || _shownAsExpanded == expanded)
        {
            return;
        }

        var animate = _shownAsExpanded is not null;
        _shownAsExpanded = expanded;

        var target = expanded ? 90d : 0d; // ChevronRight upright when collapsed, turned down when open

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

        if (Group is { } group && this.FindAscendant<TableView>() is { } tableView)
        {
            tableView.SetGroupExpanded(group, !group.IsExpanded);
            e.Handled = true;
        }
    }
}
