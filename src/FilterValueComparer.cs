using System;
using System.Collections.Generic;
using WinUI.TableView.Helpers;

namespace WinUI.TableView;

/// <summary>
/// Orders the values a column's filter flyout lists, and never throws doing it.
/// </summary>
/// <remarks>
/// The flyout used to sort with the default comparer, which throws "At least one object must implement
/// IComparable" the moment a column shows a record, a custom struct or any other type that does not implement the
/// non-generic <see cref="IComparable"/>, and "Object must be of type X" when a column mixes types. Thrown from the
/// flyout's <c>async void</c> initialisation, that took the whole process down. Here a value orders by
/// <see cref="IComparable"/> when it and its neighbour share a type that implements it, and otherwise by the text
/// the flyout shows for it, then by type name; a type that cannot be compared is traced once instead of thrown on.
/// Distinctness is not this comparer's job: the callers de-duplicate by equality first, so returning 0 for two
/// values that print alike only keeps them in insertion order.
/// </remarks>
internal sealed class FilterValueComparer : IComparer<object?>
{
    public static FilterValueComparer Instance { get; } = new();

    private static readonly HashSet<Type> _reported = [];

    public int Compare(object? x, object? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var type = x.GetType();

        if (type == y.GetType())
        {
            if (x is IComparable comparable)
            {
                try
                {
                    return comparable.CompareTo(y);
                }
                catch (ArgumentException ex)
                {
                    Report(type, ex.Message);
                }
            }
            else
            {
                Report(type, "the type does not implement IComparable");
            }
        }

        var byText = string.Compare(x.ToString(), y.ToString(), StringComparison.CurrentCultureIgnoreCase);

        return byText != 0 ? byText : string.CompareOrdinal(type.FullName, y.GetType().FullName);
    }

    private static void Report(Type type, string reason)
    {
        lock (_reported)
        {
            if (!_reported.Add(type)) return;
        }

        TableViewTrace.Write($"Filter values of type {type.FullName} are listed in text order: {reason}.");
    }
}
