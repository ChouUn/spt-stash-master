using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ChouUn.StashMaster.Core.Packing;

/// <summary>以启发式保底，限时优化大件，再填充 1×1 空位。</summary>
public sealed class CpSatPacker : IPacker
{
    public PackResult Pack(PackRequest request, double maxSeconds = 1)
    {
        var elapsed = Stopwatch.StartNew();
        var identity = new PackingIdentity(new[] { request });
        PackResult result = PackAnonymous(identity.Requests[0],
            Math.Max(0, maxSeconds - elapsed.Elapsed.TotalSeconds));
        return identity.Restore(new ContainerPackResult(new[] { result })).Grids[0];
    }

    private static PackResult PackAnonymous(PackRequest request, double maxSeconds)
    {
        var elapsed = Stopwatch.StartNew();
        PackResult baseline = Baseline(request with
        {
            CategoryOrder = Array.Empty<string>(),
        });
        string originalSpan = CategoryPacking.Describe(request, baseline);
        bool categories = CategoryPacking.Depth(request) > 0
            || CategoryOrder.Pairs(request).Count > 0;
        if (maxSeconds > 0 && categories)
        {
            baseline = CategoryPacking.Seed(request, baseline);
        }
        int area = Area(request, baseline);
        long[] available = AvailableCells(request);
        int lowerHeight = Array.FindIndex(available, cells => cells >= area);
        string facts = Describe(request, baseline, maxSeconds, lowerHeight);
        bool optimum = !baseline.Unplaced.Any(i => i.Required)
            && CategoryOrder.Penalty(request, baseline) == 0
            && area == CategoryPacking.AreaUpperBound(request)
            && Enumerable.Range(0, CategoryPacking.Depth(request)).All(d =>
                CategoryPacking.Span(request, baseline, d)
                    == CategoryPacking.LowerBound(request, area, d))
            && CategoryPacking.CoordinatesAtBound(request, baseline, area);
        string? skip = maxSeconds <= 0 ? "budget-exhausted"
            : !categories && request.Items.All(i => i.Width == 1 && i.Height == 1)
                ? "only-single-cells"
            : optimum
                && Height(request, baseline) == lowerHeight
                ? "optimum-bound" : null;
        if (skip != null)
        {
            return baseline with { Diagnostic = $"skip={skip}; {facts}" };
        }

        // 原生调用隔离在另一个方法：加载失败仍可返回已算好的启发式结果。
        try
        {
            double remaining = maxSeconds - elapsed.Elapsed.TotalSeconds;
            if (remaining <= 0)
            {
                return baseline with
                {
                    Diagnostic = $"skip=budget-after-baseline; {facts}",
                };
            }
            string status;
            PackResult? solved = categories
                ? CategoryPackingModel.Solve(new[] { request },
                    new ContainerPackResult(new[] { baseline }), remaining,
                    out status)?.Grids[0]
                : CpSatModel.Solve(request, baseline, remaining, out status);
            PackResult best = solved != null && Better(request, solved, baseline)
                ? solved : baseline;
            return best with
            {
                Diagnostic = $"cp-sat {status}, {elapsed.ElapsedMilliseconds} ms, " +
                    $"area {area}->{Area(request, best)}, " +
                    $"movable-rows {Height(request, baseline)}" +
                    $"->{Height(request, best)}, " +
                    $"category-span {originalSpan}" +
                    $"->{CategoryPacking.Describe(request, best)}; "
                    + facts,
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException
            || ex is TypeInitializationException
            || ex is System.IO.FileNotFoundException
            || ex is System.IO.FileLoadException)
        {
            return baseline with
            {
                Diagnostic = $"solver unavailable; {facts}: {ex}",
                Warning = "求解器加载失败，已采用保底布局",
            };
        }
    }

    /// <summary>比较新排布与原位填空；必留物品不能被启发式遗漏。</summary>
    private static PackResult Baseline(PackRequest request)
    {
        var heuristic = new HeuristicPacker();
        PackResult fresh = heuristic.Pack(request);
        if (request.Current.Count == 0)
        {
            return fresh;
        }
        var currentIds = new HashSet<string>(request.Current.Select(p => p.Id));
        var rest = request with
        {
            Fixed = request.Fixed.Concat(Blocks(request, request.Current)).ToArray(),
            Items = request.Items.Where(i => !currentIds.Contains(i.Id)).ToArray(),
        };
        PackResult filled = heuristic.Pack(rest);
        var preserved = new PackResult(
            request.Current.Concat(filled.Placements).ToArray(), filled.Unplaced);
        // 完整评分相同时保留原布局，避免等价几何与身份反复换位。
        return fresh.Unplaced.Any(i => i.Required)
            || (!preserved.Unplaced.Any(i => i.Required) && !Better(request, fresh, preserved))
            ? preserved : fresh;
    }

    internal static bool Better(PackRequest request, PackResult next, PackResult before)
    {
        if (next.Unplaced.Any(i => i.Required))
        {
            return false;
        }
        int difference = Area(request, next) - Area(request, before);
        if (difference == 0 && request.CategoryOrder.Count > 0)
        {
            int ordered = CategoryOrder.Compare(request, next, before);
            if (ordered != 0) { return ordered < 0; }
        }
        return difference > 0 || (difference == 0
            && (Height(request, next) < Height(request, before)
                || (Height(request, next) == Height(request, before)
                    && CategoryPacking.Compare(request, next, before) < 0)));
    }

    internal static int Area(PackRequest request, PackResult result)
    {
        var ids = new HashSet<string>(result.Placements.Select(p => p.Id));
        return request.Items.Where(i => ids.Contains(i.Id))
            .Sum(i => i.Width * i.Height);
    }

    internal static int Height(PackRequest request, PackResult result) =>
        Blocks(request, result.Placements)
            .Select(b => b.Y + b.Height).DefaultIfEmpty().Max();

    /// <summary>每个高度内的真实可用格数；固定物品只扣面积，不抬高优化目标。</summary>
    internal static long[] AvailableCells(PackRequest request)
    {
        var available = new long[request.Height + 1];
        for (int row = 0; row < request.Height; row++)
        {
            int blocked = request.Fixed.Where(b => b.Y <= row && row < b.Y + b.Height)
                .Sum(b => b.Width);
            available[row + 1] = available[row] + request.Width - blocked;
        }
        return available;
    }

    /// <summary>输出退出判断的输入，区分固定障碍、可移动高度和实际变化数量。</summary>
    private static string Describe(
        PackRequest request, PackResult baseline, double seconds, int lowerHeight)
    {
        var currentIds = new HashSet<string>(request.Current.Select(p => p.Id));
        var current = new PackResult(request.Current,
            request.Items.Where(i => !currentIds.Contains(i.Id)).ToArray());
        var positions = request.Current.ToDictionary(p => p.Id);
        int changed = baseline.Placements.Count(p =>
            !positions.TryGetValue(p.Id, out Placement? old) || old != p);
        int fixedBottom = request.Fixed.Select(b => b.Y + b.Height)
            .DefaultIfEmpty().Max();
        return $"grid={request.Width}x{request.Height}, items={request.Items.Count}, " +
            $"large={request.Items.Count(i => i.Width != 1 || i.Height != 1)}, " +
            $"fixed={request.Fixed.Count}, fixed-bottom={fixedBottom}, " +
            $"current-movable-rows={Height(request, current)}, " +
            $"current-category-span={CategoryPacking.Describe(request, current)}, " +
            $"baseline-movable-rows={Height(request, baseline)}, " +
            $"lower={lowerHeight}, " +
            $"baseline-area={Area(request, baseline)}, " +
            $"category-span={CategoryPacking.Describe(request, baseline)}, " +
            $"unplaced={baseline.Unplaced.Count}, " +
            $"baseline-slot-changes={changed}, " +
            FormattableString.Invariant($"budget={seconds:F3}s");
    }

    internal static IEnumerable<FixedBlock> Blocks(
        PackRequest request, IReadOnlyList<Placement> placements)
    {
        var items = request.Items.ToDictionary(i => i.Id);
        return placements.Select(p => new FixedBlock(p.X, p.Y,
            p.Rotated ? items[p.Id].Height : items[p.Id].Width,
            p.Rotated ? items[p.Id].Width : items[p.Id].Height));
    }
}
