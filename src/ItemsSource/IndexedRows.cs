using System;
using System.Collections.Generic;

namespace WinUI.TableView;

/// <summary>
/// An order-statistic sequence (implicit treap with parent pointers) used by <see cref="TreeTableViewSource"/> as
/// its flat-row store. Unlike a <see cref="List{T}"/>, every operation the adapter needs under heavy streaming is
/// logarithmic: select-by-index, rank-of-handle (IndexOf), insert-at and remove — no O(n) scans or shifts.
/// </summary>
internal sealed class IndexedRows
{
    /// <summary>
    /// The handle callers keep per row; stable across other rows' inserts/removes.
    /// </summary>
    internal sealed class Row
    {
        internal Row(object value, int priority)
        {
            Value = value;
            Priority = priority;
        }

        public object Value { get; }
        internal int Priority { get; }
        internal int Size { get; set; } = 1;
        internal Row? Left { get; set; }
        internal Row? Right { get; set; }
        internal Row? Parent { get; set; }
    }

    // Fixed seed: reproducible shapes, and the priorities only guard balance, not correctness.
    private readonly Random _random = new(0x5EED);
    private Row? _root;

    public int Count => SizeOf(_root);

    public object this[int index] => SelectAt(index).Value;

    /// <summary>
    /// Inserts a value so it becomes the row at the given index; returns its handle.
    /// </summary>
    public Row InsertAt(int index, object value)
    {
        if ((uint)index > (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var row = new Row(value, _random.Next());

        if (_root is null)
        {
            _root = row;
            return row;
        }

        // Descend to the in-order position, growing sizes on the way, and attach as a leaf.
        var current = _root;
        var remaining = index;

        while (true)
        {
            current.Size++;
            var leftSize = SizeOf(current.Left);

            if (remaining <= leftSize)
            {
                if (current.Left is null)
                {
                    current.Left = row;
                    row.Parent = current;
                    break;
                }

                current = current.Left;
            }
            else
            {
                remaining -= leftSize + 1;

                if (current.Right is null)
                {
                    current.Right = row;
                    row.Parent = current;
                    break;
                }

                current = current.Right;
            }
        }

        // Restore the heap property by rotating the new row up.
        while (row.Parent is { } parent && row.Priority > parent.Priority)
        {
            if (ReferenceEquals(parent.Left, row))
            {
                RotateRight(parent);
            }
            else
            {
                RotateLeft(parent);
            }
        }

        return row;
    }

    /// <summary>
    /// Removes the row behind the handle.
    /// </summary>
    public void Remove(Row row)
    {
        // Rotate the row down until it is a leaf, then detach and shrink ancestor sizes.
        while (row.Left is not null || row.Right is not null)
        {
            if (row.Right is null || (row.Left is not null && row.Left.Priority > row.Right.Priority))
            {
                RotateRight(row);
            }
            else
            {
                RotateLeft(row);
            }
        }

        var parent = row.Parent;

        if (parent is null)
        {
            _root = null;
            return;
        }

        if (ReferenceEquals(parent.Left, row))
        {
            parent.Left = null;
        }
        else
        {
            parent.Right = null;
        }

        row.Parent = null;

        for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            ancestor.Size--;
        }
    }

    /// <summary>
    /// The current index of the row behind the handle (rank query).
    /// </summary>
    public int IndexOf(Row row)
    {
        var index = SizeOf(row.Left);

        for (var current = row; current.Parent is { } parent; current = parent)
        {
            if (ReferenceEquals(parent.Right, current))
            {
                index += SizeOf(parent.Left) + 1;
            }
        }

        return index;
    }

    /// <summary>
    /// The row handle at the given index (select query).
    /// </summary>
    public Row SelectAt(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var current = _root!;

        while (true)
        {
            var leftSize = SizeOf(current.Left);

            if (index < leftSize)
            {
                current = current.Left!;
            }
            else if (index == leftSize)
            {
                return current;
            }
            else
            {
                index -= leftSize + 1;
                current = current.Right!;
            }
        }
    }

    /// <summary>
    /// In-order (flat-index order) enumeration of the row values.
    /// </summary>
    public IEnumerator<object> GetEnumerator()
    {
        var stack = new Stack<Row>();
        var current = _root;

        while (current is not null || stack.Count > 0)
        {
            while (current is not null)
            {
                stack.Push(current);
                current = current.Left;
            }

            current = stack.Pop();
            yield return current.Value;
            current = current.Right;
        }
    }

    private static int SizeOf(Row? row) => row?.Size ?? 0;

    private static void Refresh(Row row) => row.Size = 1 + SizeOf(row.Left) + SizeOf(row.Right);

    private void RotateRight(Row parent)
    {
        var pivot = parent.Left!;
        ReplaceInParent(parent, pivot);

        parent.Left = pivot.Right;
        parent.Left?.Parent = parent;

        pivot.Right = parent;
        parent.Parent = pivot;

        Refresh(parent);
        Refresh(pivot);
    }

    private void RotateLeft(Row parent)
    {
        var pivot = parent.Right!;
        ReplaceInParent(parent, pivot);

        parent.Right = pivot.Left;
        parent.Right?.Parent = parent;

        pivot.Left = parent;
        parent.Parent = pivot;

        Refresh(parent);
        Refresh(pivot);
    }

    private void ReplaceInParent(Row oldRow, Row newRow)
    {
        var grandParent = oldRow.Parent;
        newRow.Parent = grandParent;

        if (grandParent is null)
        {
            _root = newRow;
        }
        else if (ReferenceEquals(grandParent.Left, oldRow))
        {
            grandParent.Left = newRow;
        }
        else
        {
            grandParent.Right = newRow;
        }
    }
}
