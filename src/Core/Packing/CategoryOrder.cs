using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ChouUn.StashMaster.Core.Packing;

/// <summary>约束统一树顶层覆盖区域的中线顺序，允许边界重叠与穿插。</summary>
internal static class CategoryOrder
{
    internal sealed record Pair(string Before, string After);

    internal static IReadOnlyList<Pair> Pairs(PackRequest request)
    {
        if (request.CategoryOrder.Count == 0 || request.Items.Any(i => !i.Required))
            return Array.Empty<Pair>();
        var ranks = new HashSet<string>(request.CategoryOrder, StringComparer.Ordinal);
        string[] ordered = PackingTree.For(request).Roots.Select(node => node.SortType).ToArray();
        return ordered.SelectMany((type, index) => ranks.Contains(type)
            ? ordered.Skip(index + 1).Select(next => new Pair(type, next))
            : Enumerable.Empty<Pair>()).ToArray();
    }

    /// <summary>累计类型反序量，用于候选比较和未满足顺序时的回退判断。</summary>
    internal static long Penalty(PackRequest request, PackResult result)
    {
        IReadOnlyList<Pair> pairs = Pairs(request);
        if (pairs.Count == 0) { return 0; }
        long penalty = 0;
        var positions = result.Placements.ToDictionary(p => p.Id);
        var centers = PackingTree.For(request).Roots
            .Select(node => (node.SortType, Extent: PackingTree.Measure(node, positions)))
            .Where(entry => entry.Extent.End > 0)
            .ToDictionary(entry => entry.SortType,
                entry => entry.Extent.First + entry.Extent.End);
        foreach (Pair pair in pairs)
        {
            if (centers.TryGetValue(pair.Before, out int first)
                && centers.TryGetValue(pair.After, out int second))
                penalty += Math.Max(0, first - second);
        }
        return penalty;
    }

    /// <summary>启用时先满足类别顺序，再比较原有空间与聚合目标。</summary>
    internal static BigInteger Score(IReadOnlyList<PackRequest> requests,
        ContainerPackResult result)
    {
        BigInteger score = requests.Select((r, i) =>
                CategoryPacking.Score(r, result.Grids[i]))
            .Aggregate(BigInteger.Zero, (sum, value) => sum + value);
        if (requests.All(r => r.CategoryOrder.Count == 0)) { return score; }
        BigInteger quality = score;
        score = requests.Select((r, i) => Penalty(r, result.Grids[i])).Sum();
        BigInteger qualityUpper = requests.Select(r =>
        {
            BigInteger upper = r.Height
                + (long)CategoryPacking.AreaUpperBound(r) * (r.Height + 1L);
            for (int level = 0; level < CategoryPacking.Depth(r); level++)
                upper = upper * (CategoryPacking.UpperBound(r, level) + 1)
                    + CategoryPacking.UpperBound(r, level);
            upper = upper * (CategoryPacking.CoordinateUpperBound(r, false) + 1)
                + CategoryPacking.CoordinateUpperBound(r, false);
            upper = upper * (CategoryPacking.CoordinateUpperBound(r, true) + 1)
                + CategoryPacking.CoordinateUpperBound(r, true);
            return upper;
        }).Aggregate(BigInteger.Zero, (sum, value) => sum + value);
        return score * (qualityUpper + 1) + quality;
    }

    internal static int Compare(PackRequest request, PackResult next, PackResult before)
    {
        return Penalty(request, next).CompareTo(Penalty(request, before));
    }

    /// <summary>给求解器提供按配置排列的合法初始布局，仍按完整质量择优。</summary>
    internal static PackResult Seed(PackRequest request, PackResult baseline)
    {
        if (Pairs(request).Count == 0) { return baseline; }
        var ranks = request.CategoryOrder.Select((id, rank) => (id, rank))
            .ToDictionary(p => p.id, p => p.rank);
        int Key(PackItem item) => ranks.TryGetValue(item.SortType, out int rank)
            ? rank : int.MaxValue;
        var items = request.Items.ToDictionary(i => i.Id);
        int End(Placement p) => p.Y
            + (p.Rotated ? items[p.Id].Width : items[p.Id].Height);
        int height = CpSatPacker.Height(request, baseline);
        // 镜像和横向断面重排保留块内几何；它们只是提示，仍接受全局质量比较。
        Placement[] mirrored = baseline.Placements.Select(p => p with
        {
            Y = height - End(p),
        }).ToArray();
        var bands = new List<List<Placement>>();
        var cuts = new List<int>();
        int end = -1;
        foreach (Placement p in baseline.Placements.OrderBy(p => p.Y))
        {
            if (p.Y >= end)
            {
                bands.Add(new List<Placement>());
                cuts.Add(p.Y);
            }
            bands[bands.Count - 1].Add(p);
            end = Math.Max(end, End(p));
        }
        var rearranged = new List<Placement>();
        int row = 0;
        foreach (List<Placement> band in bands.OrderBy(b => Key(items[
            b.OrderByDescending(p => items[p.Id].Width * items[p.Id].Height)
                .First().Id])))
        {
            int first = band.Min(p => p.Y);
            int last = band.Max(End);
            rearranged.AddRange(band.Select(p => p with { Y = p.Y - first + row }));
            row += last - first;
        }
        var candidates = new List<IReadOnlyList<Placement>> { mirrored, rearranged };
        foreach (int cut in cuts.Where(c => c > 0))
        {
            candidates.Add(baseline.Placements.Select(p => p.Y < cut
                ? p with { Y = cut - End(p) } : p).ToArray());
            candidates.Add(baseline.Placements.Select(p => p.Y >= cut
                ? p with { Y = cut + height - End(p) } : p).ToArray());
            candidates.Add(baseline.Placements.Select(p => p with
            {
                Y = p.Y < cut ? p.Y + height - cut : p.Y - cut,
            }).ToArray());
        }
        foreach (IReadOnlyList<Placement> placements in candidates)
        {
            var candidate = baseline with { Placements = placements };
            bool hitsFixed = CpSatPacker.Blocks(request, candidate.Placements)
                .Any(a => request.Fixed.Any(b => a.X < b.X + b.Width
                    && b.X < a.X + a.Width && a.Y < b.Y + b.Height
                    && b.Y < a.Y + a.Height));
            if (!hitsFixed && CpSatPacker.Better(request, candidate, baseline))
                baseline = candidate;
        }
        PackResult ordered = Hint(request, baseline);
        return CpSatPacker.Better(request, ordered, baseline) ? ordered : baseline;
    }

    /// <summary>按新顺序提供完整布局提示，正式目标仍由求解模型决定。</summary>
    internal static PackResult Hint(PackRequest request, PackResult baseline)
    {
        if (Pairs(request).Count == 0) { return baseline; }
        var ranks = request.CategoryOrder.Select((id, rank) => (id, rank))
            .ToDictionary(p => p.id, p => p.rank);
        // 从两端按类型构造提示，并尝试横放、竖放；只保留合法且更好的完整布局。
        foreach (bool reverse in new[] { false, true })
        {
            for (int orientation = 0; orientation < 3; orientation++)
            {
                var rotated = new HashSet<string>(request.Items.Where(i =>
                    orientation == 1 && i.Width < i.Height
                        || orientation == 2 && i.Width > i.Height).Select(i => i.Id));
                PackItem[] items = request.Items.Select(i => rotated.Contains(i.Id)
                        ? i with { Width = i.Height, Height = i.Width } : i)
                    .OrderBy(i => (reverse ? -1L : 1L)
                        * (ranks.TryGetValue(i.SortType, out int rank)
                            ? rank : int.MaxValue))
                    .ThenByDescending(i => i.Width * i.Height)
                    .ThenBy(i => i.TemplateId, StringComparer.Ordinal)
                    .ThenBy(i => i.Id, StringComparer.Ordinal).ToArray();
                PackRequest oriented = request with { Items = items };
                var byId = items.ToDictionary(i => i.Id);
                foreach (PackResult candidate in new[]
                {
                    HeuristicPacker.PackOrdered(oriented, items),
                    Sequential(oriented),
                })
                {
                    if (!candidate.Complete) { continue; }
                    int height = CpSatPacker.Height(oriented, candidate);
                    PackResult restored = candidate with
                    {
                        Placements = candidate.Placements.Select(p => p with
                        {
                            Y = reverse ? height - p.Y - (p.Rotated
                                ? byId[p.Id].Width : byId[p.Id].Height) : p.Y,
                            Rotated = p.Rotated ^ rotated.Contains(p.Id),
                        }).ToArray(),
                    };
                    bool hitsFixed = CpSatPacker.Blocks(request, restored.Placements)
                        .Any(a => request.Fixed.Any(b => a.X < b.X + b.Width
                            && b.X < a.X + a.Width && a.Y < b.Y + b.Height
                            && b.Y < a.Y + a.Height));
                    if (!hitsFixed && CpSatPacker.Better(request, restored, baseline))
                        baseline = restored;
                }
            }
        }
        return baseline;
    }

    /// <summary>按类型逐组排入，共享边界行但不向早期类别留下的空洞回填。</summary>
    private static PackResult Sequential(PackRequest request)
    {
        var placements = new List<Placement>();
        int center = 0;
        foreach (var group in request.Items.GroupBy(i => i.SortType))
        {
            PackItem[] members = group.ToArray();
            int minHeight = (members.Sum(i => i.Width * i.Height)
                + request.Width - 1) / request.Width;
            // 只限制下一类型的最早起始行，保证中线不倒序，仍允许边界共享行。
            int floor = Math.Max(0, (center - minHeight + 1) / 2);
            PackRequest partial = request with
            {
                Items = members,
                Fixed = request.Fixed.Concat(CpSatPacker.Blocks(request, placements))
                    .Concat(new[] { new FixedBlock(0, 0, request.Width, floor) })
                    .ToArray(),
            };
            PackResult packed = HeuristicPacker.PackOrdered(partial, members);
            if (!packed.Complete)
                return new PackResult(Array.Empty<Placement>(), request.Items);
            placements.AddRange(packed.Placements);
            center = packed.Placements.Min(p => p.Y)
                + CpSatPacker.Height(partial, packed);
        }
        return new PackResult(placements, Array.Empty<PackItem>());
    }
}
