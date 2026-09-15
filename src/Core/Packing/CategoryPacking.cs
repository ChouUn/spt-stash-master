using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ChouUn.StashMaster.Core.Packing;

/// <summary>统一树逐层统计覆盖行跨度：SortType 顶层优先，固定障碍不计入。</summary>
public static class CategoryPacking
{
    /// <summary>构造空间不退步的层级提示，不限制全局模型后续搜索。</summary>
    internal static PackResult Seed(PackRequest request, PackResult baseline)
    {
        baseline = CategoryOrder.Seed(request, baseline);
        PackingTree tree = PackingTree.For(request);
        foreach (bool reverse in new[] { false, true })
        {
            var ranks = tree.Levels.Select((level, depth) =>
            {
                bool orderedRoot = depth == 0 && request.CategoryOrder.Count > 0;
                var groups = orderedRoot ? level.ToArray()
                    : level.OrderByDescending(node => node.Items.Sum(i => i.Width * i.Height))
                        .ThenBy(node => node.Key, StringComparer.Ordinal).ToArray();
                return (reverse && !orderedRoot ? groups.Reverse() : groups)
                    .Select((node, index) => (node.Key, index))
                    .ToDictionary(p => p.Key, p => p.index);
            }).ToArray();
            var keys = request.Items.ToDictionary(i => i.Id, i => string.Join("/",
                tree.Paths[i.Id].Select(node => ranks[node.Depth][node.Key].ToString("D5"))));
            // 比较完整树路径、先大件以及顶层内部先大件三种合法提示。
            for (int mode = 0; mode < 3; mode++)
            {
                PackResult seed = HeuristicPacker.PackOrdered(request, request.Items
                    .OrderBy(i => mode == 1 && i.Width * i.Height == 1)
                    .ThenBy(i => mode == 2 ? ranks[0][tree.Paths[i.Id][0].Key] : 0)
                    .ThenBy(i => mode == 2 && i.Width * i.Height == 1)
                    .ThenBy(i => keys[i.Id], StringComparer.Ordinal)
                    .ThenByDescending(i => i.Width * i.Height)
                    .ThenBy(i => i.TemplateId, StringComparer.Ordinal)
                    .ThenBy(i => i.Id, StringComparer.Ordinal));
                if (CpSatPacker.Better(request, seed, baseline))
                {
                    baseline = seed;
                }
            }
        }
        return CategoryOrder.Seed(request, baseline);
    }

    public static int Span(PackRequest request, PackResult result, int level = 0)
    {
        var positions = result.Placements.ToDictionary(p => p.Id);
        return Level(request, level).Sum(node => Span(node, positions));
    }

    public static int[] Spans(PackRequest request, PackResult result)
    {
        var positions = result.Placements.ToDictionary(p => p.Id);
        return PackingTree.For(request).Levels.Select(level =>
            level.Sum(node => Span(node, positions))).ToArray();
    }

    private static int Span(PackingTree.Node node,
        IReadOnlyDictionary<string, Placement> positions)
    {
        var extent = PackingTree.Measure(node, positions);
        return extent.End == 0 ? 0 : extent.End - extent.First - 1;
    }

    /// <summary>先算各网格完整分数，再求和；与联合模型使用相同目标。</summary>
    internal static int Compare(IReadOnlyList<PackRequest> requests,
        ContainerPackResult next, ContainerPackResult before)
        => (CategoryOrder.Score(requests, next)
            - CategoryOrder.Score(requests, before)).Sign;

    internal static int Compare(PackRequest request, PackResult next, PackResult before)
    {
        int[] afterSpans = Spans(request, next);
        int[] beforeSpans = Spans(request, before);
        for (int d = 0; d < afterSpans.Length; d++)
        {
            int difference = afterSpans[d] - beforeSpans[d];
            if (difference != 0) { return difference; }
        }
        int rows = CoordinateSum(request, next, false).CompareTo(CoordinateSum(request, before, false));
        return rows != 0 ? rows
            : CoordinateSum(request, next, true).CompareTo(CoordinateSum(request, before, true));
    }

    /// <summary>仅用本网格的类别上界确定倍率；任意精度避免深层类别溢出。</summary>
    internal static BigInteger Score(PackRequest request, PackResult result)
    {
        BigInteger score = CpSatPacker.Height(request, result)
            + (long)(AreaUpperBound(request) - CpSatPacker.Area(request, result))
                * (request.Height + 1L);
        int[] spans = Spans(request, result);
        for (int level = 0; level < spans.Length; level++)
        {
            score = score * (UpperBound(request, level) + 1) + spans[level];
        }
        score = score * (CoordinateUpperBound(request, false) + 1)
            + CoordinateSum(request, result, false);
        return score * (CoordinateUpperBound(request, true) + 1)
            + CoordinateSum(request, result, true);
    }

    internal static long UpperBound(PackRequest request, int level) =>
        (request.Height - 1L) * Level(request, level).Count;

    /// <summary>可移动物品占用格子的坐标和；固定障碍贡献恒定，不计入目标。</summary>
    internal static long CoordinateSum(PackRequest request, PackResult result, bool horizontal)
    {
        var items = request.Items.ToDictionary(i => i.Id);
        long sum = 0;
        foreach (Placement p in result.Placements)
        {
            PackItem item = items[p.Id];
            int extent = horizontal ? (p.Rotated ? item.Height : item.Width)
                : (p.Rotated ? item.Width : item.Height);
            long area = (long)item.Width * item.Height;
            sum += area * (horizontal ? p.X : p.Y) + area * (extent - 1) / 2;
        }
        return sum;
    }

    internal static long CoordinateUpperBound(PackRequest request, bool horizontal) =>
        (long)request.Width * request.Height
            * ((horizontal ? request.Width : request.Height) - 1) / 2;

    /// <summary>忽略物品形状，优先占用坐标最小的可用格子，得到安全下界。</summary>
    internal static long CoordinateLowerBound(PackRequest request, int area, bool horizontal)
    {
        int length = horizontal ? request.Width : request.Height;
        int breadth = horizontal ? request.Height : request.Width;
        long sum = 0;
        for (int coordinate = 0; coordinate < length && area > 0; coordinate++)
        {
            int available = breadth;
            foreach (FixedBlock block in request.Fixed)
            {
                int start = horizontal ? block.X : block.Y;
                int extent = horizontal ? block.Width : block.Height;
                if (coordinate >= start && coordinate < start + extent)
                    available -= horizontal ? block.Height : block.Width;
            }
            int used = Math.Min(area, Math.Max(0, available));
            sum += (long)used * coordinate;
            area -= used;
        }
        return sum;
    }

    internal static bool CoordinatesAtBound(PackRequest request, PackResult result, int area) =>
        CoordinateSum(request, result, false) == CoordinateLowerBound(request, area, false)
        && CoordinateSum(request, result, true) == CoordinateLowerBound(request, area, true);

    /// <summary>
    /// 由必留面积和其他类别的最大供给量推导每类不可避免的面积。
    /// 可选物品可被舍弃；几何放置暂时放宽，因此是安全下界而非布局承诺。
    /// </summary>
    internal static int LowerBound(PackRequest request, int area, int level)
    {
        int total = request.Items.Sum(i => i.Width * i.Height);
        return Level(request, level).Sum(node =>
        {
            IReadOnlyList<PackItem> group = node.Items;
            int needed = Math.Max(group.Where(i => i.Required)
                .Sum(i => i.Width * i.Height),
                area - (total - group.Sum(i => i.Width * i.Height)));
            int rows = Math.Max((needed + request.Width - 1) / request.Width,
                group.Where(i => i.Required).Select(i => Math.Min(i.Width, i.Height))
                    .DefaultIfEmpty().Max());
            return Math.Max(0, rows - 1);
        });
    }

    internal static int AreaUpperBound(PackRequest request) => Math.Min(
        request.Items.Sum(i => i.Width * i.Height),
        (int)CpSatPacker.AvailableCells(request)[request.Height]);

    internal static int Depth(PackRequest request) => PackingTree.For(request).Levels.Count;

    private static IReadOnlyList<PackingTree.Node> Level(PackRequest request, int level)
    {
        var levels = PackingTree.For(request).Levels;
        return level < levels.Count ? levels[level] : Array.Empty<PackingTree.Node>();
    }

    internal static string Describe(PackRequest request, PackResult result) =>
        "[" + string.Join(",", Spans(request, result)) + "]";
}
