using System;
using System.Collections.Generic;
using System.Linq;
using Google.OrTools.Sat;

namespace ChouUn.StashMaster.Core.Packing;

/// <summary>等价位置槽按网格、行列排序，缺席槽在末尾，消除身份互换的重复解。</summary>
internal static class PackingSymmetry
{
    public static void Add(CpModel model, IReadOnlyList<PackRequest> requests,
        IEnumerable<(int Grid, CpSatModel.Rectangle Rect)> rectangles,
        ContainerPackResult baseline)
    {
        // 几何等价由尺寸、模板叶节点和网格资格决定；实例身份留给结果回填。
        var groups = requests.SelectMany((request, grid) =>
        {
            PackingTree tree = PackingTree.For(request);
            return request.Items.Select(item =>
            {
                var path = tree.Paths[item.Id];
                string leaf = path[path.Count - 1].Key;
                string key = $"{grid}:{item.Required}:{item.Width},{item.Height}:{leaf.Length}:{leaf}";
                return (Item: item, Grid: grid, Key: key);
            });
        }).GroupBy(entry => entry.Item.Id).Select(group =>
            (Item: group.First().Item, Key: string.Join(";", group.OrderBy(entry => entry.Grid)
                .Select(entry => entry.Key))))
            .GroupBy(entry => entry.Key).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Select(entry => entry.Item).ToArray()).ToArray();
        Add(model, groups, rectangles, baseline);
    }

    /// <summary>由调用者明确等价分组，压紧提示可仅按类型和尺寸消除重复解。</summary>
    public static void Add(CpModel model, IReadOnlyList<PackItem[]> groups,
        IEnumerable<(int Grid, CpSatModel.Rectangle Rect)> rectangles,
        ContainerPackResult baseline)
    {
        var byId = rectangles.GroupBy(r => r.Rect.Item.Id)
            .ToDictionary(g => g.Key, g => g.ToArray());
        var hints = baseline.Grids.SelectMany((r, grid) => r.Placements.Select(p =>
            (p.Id, Placement: p, Grid: grid))).ToDictionary(p => p.Id);
        foreach (PackItem[] members in groups)
        {
            PackItem[] group = members
                .OrderBy(i => hints.TryGetValue(i.Id, out var p)
                    ? p.Grid : int.MaxValue)
                .ThenBy(i => hints.TryGetValue(i.Id, out var p) ? p.Placement.Y : 0)
                .ThenBy(i => hints.TryGetValue(i.Id, out var p) ? p.Placement.X : 0)
                .ToArray();
            if (group.Length < 2 || !byId.ContainsKey(group[0].Id)) { continue; }
            for (int i = 1; i < group.Length; i++)
            {
                var previous = byId[group[i - 1].Id];
                var next = byId[group[i].Id];
                model.Add(LinearExpr.Sum(previous.Select(v => v.Rect.Present))
                    >= LinearExpr.Sum(next.Select(v => v.Rect.Present)));
                foreach (var a in previous)
                {
                    foreach (var b in next)
                    {
                        if (a.Grid > b.Grid)
                        {
                            model.AddBoolOr(new ILiteral[]
                                { a.Rect.Present.Not(), b.Rect.Present.Not() });
                        }
                        else if (a.Grid == b.Grid)
                        {
                            CpSatModel.Rectangle left = a.Rect;
                            CpSatModel.Rectangle right = b.Rect;
                            model.Add(left.Y <= right.Y)
                                .OnlyEnforceIf(new ILiteral[]
                                    { left.Present, right.Present });
                            BoolVar sameRow = model.NewBoolVar("same-row");
                            model.Add(left.Y == right.Y).OnlyEnforceIf(sameRow);
                            model.Add(left.Y != right.Y).OnlyEnforceIf(sameRow.Not());
                            model.Add(left.X <= right.X).OnlyEnforceIf(new ILiteral[]
                                { left.Present, right.Present, sameRow });
                            int firstY = hints.TryGetValue(left.Item.Id, out var p)
                                && p.Grid == a.Grid ? p.Placement.Y : 0;
                            int nextY = hints.TryGetValue(right.Item.Id, out var q)
                                && q.Grid == b.Grid ? q.Placement.Y : 0;
                            model.AddHint(sameRow, firstY == nextY);
                        }
                    }
                }
            }
        }
    }
}
