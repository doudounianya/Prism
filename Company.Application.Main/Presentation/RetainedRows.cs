/*
 * 文件职责与维护说明：
 * 以业务键同步 ObservableCollection：只删除消失行、插入新行或移动已有行，并原位更新同一业务对象。
 * 这样可保留界面选择和控件实例，避免每次刷新都重置 ItemsSource。
 */
using System.Collections.ObjectModel;

namespace Company.Application.Main.Presentation;

/// <summary>按业务键增删和移动行，不重置整个 ItemsSource。</summary>
internal static class RetainedRows
{
    // 先逆序删除已消失的业务键，避免删除时索引前移；再按新顺序插入或移动。
    // 同键已有行交给 apply 原位更新，保留绑定对象、选择及视觉容器。
    public static void Synchronize<T, TKey>(ObservableCollection<T> rows, IReadOnlyList<T> incoming,
        Func<T, TKey> key, Action<T, T> apply) where TKey : notnull
    {
        var keys = incoming.Select(key).ToHashSet();
        for (int i = rows.Count - 1; i >= 0; i--)
            if (!keys.Contains(key(rows[i]))) rows.RemoveAt(i);
        for (int i = 0; i < incoming.Count; i++)
        {
            var next = incoming[i]; int found = -1;
            for (int j = i; j < rows.Count; j++)
                if (EqualityComparer<TKey>.Default.Equals(key(rows[j]), key(next))) { found = j; break; }
            if (found < 0) rows.Insert(i, next);
            else { if (found != i) rows.Move(found, i); apply(rows[i], next); }
        }
    }
}
