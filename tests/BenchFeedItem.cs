using System;
using System.ComponentModel;
using System.Linq;

namespace WinUI.TableView.Tests;

/// <summary>
/// One row of a blotter-shaped feed for the benchmarks: seventy numeric columns, each its own property, mutated in
/// place through <see cref="INotifyPropertyChanged"/> one column at a time — the shape of the sample page's
/// <c>PerfRow</c> and of the consuming app's items. The pan benchmarks' <c>BenchItem</c> has two properties shared
/// by all columns, so one change there re-evaluates every cell of the row at once, which is not what a feed does.
/// </summary>
public sealed class BenchFeedItem : INotifyPropertyChanged, IBenchFeedItem
{
    public const int ValueCount = 70;

    public static readonly string[] Names = [.. Enumerable.Range(0, ValueCount).Select(i => $"C{i:D2}")];

    // One args instance per property: the feed raises thousands of these a second and must not allocate for it.
    private static readonly PropertyChangedEventArgs[] Args = [.. Names.Select(name => new PropertyChangedEventArgs(name))];

    private double[]? _values; // allocated on the first tick; rows that are never on screen never pay for it

    public BenchFeedItem(int index)
    {
        Index = index;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }

    public void Tick(int column, double delta)
    {
        _values ??= Enumerable.Repeat(double.NaN, ValueCount).ToArray();
        _values[column] = Math.Round(Get(column) + delta, 2);
        PropertyChanged?.Invoke(this, Args[column]);
    }

    private double Get(int column)
        => _values is { } values && !double.IsNaN(values[column]) ? values[column] : Seed(column);

    // Deterministic and free: the same row shows the same numbers every run, without a byte stored per row.
    private double Seed(int column) => 100 + ((Index * 31 + column * 17) % 1000) / 10.0;

    public double C00 => Get(0);
    public double C01 => Get(1);
    public double C02 => Get(2);
    public double C03 => Get(3);
    public double C04 => Get(4);
    public double C05 => Get(5);
    public double C06 => Get(6);
    public double C07 => Get(7);
    public double C08 => Get(8);
    public double C09 => Get(9);
    public double C10 => Get(10);
    public double C11 => Get(11);
    public double C12 => Get(12);
    public double C13 => Get(13);
    public double C14 => Get(14);
    public double C15 => Get(15);
    public double C16 => Get(16);
    public double C17 => Get(17);
    public double C18 => Get(18);
    public double C19 => Get(19);
    public double C20 => Get(20);
    public double C21 => Get(21);
    public double C22 => Get(22);
    public double C23 => Get(23);
    public double C24 => Get(24);
    public double C25 => Get(25);
    public double C26 => Get(26);
    public double C27 => Get(27);
    public double C28 => Get(28);
    public double C29 => Get(29);
    public double C30 => Get(30);
    public double C31 => Get(31);
    public double C32 => Get(32);
    public double C33 => Get(33);
    public double C34 => Get(34);
    public double C35 => Get(35);
    public double C36 => Get(36);
    public double C37 => Get(37);
    public double C38 => Get(38);
    public double C39 => Get(39);
    public double C40 => Get(40);
    public double C41 => Get(41);
    public double C42 => Get(42);
    public double C43 => Get(43);
    public double C44 => Get(44);
    public double C45 => Get(45);
    public double C46 => Get(46);
    public double C47 => Get(47);
    public double C48 => Get(48);
    public double C49 => Get(49);
    public double C50 => Get(50);
    public double C51 => Get(51);
    public double C52 => Get(52);
    public double C53 => Get(53);
    public double C54 => Get(54);
    public double C55 => Get(55);
    public double C56 => Get(56);
    public double C57 => Get(57);
    public double C58 => Get(58);
    public double C59 => Get(59);
    public double C60 => Get(60);
    public double C61 => Get(61);
    public double C62 => Get(62);
    public double C63 => Get(63);
    public double C64 => Get(64);
    public double C65 => Get(65);
    public double C66 => Get(66);
    public double C67 => Get(67);
    public double C68 => Get(68);
    public double C69 => Get(69);
}

/// <summary>What the feed benchmark needs from an item, whichever binding path the item exposes.</summary>
public interface IBenchFeedItem
{
    void Tick(int column, double delta);
}
