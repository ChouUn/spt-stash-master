using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ChouUn.StashMaster.Core.Packing;

/// <summary>最终排布一次联合所有网格；物品归属不变，整个模型共用剩余预算。</summary>
internal sealed class GlobalPacker
{
    private const double MaxCompactionSeconds = 0.75;
    private readonly IPacker _single;

    public GlobalPacker(IPacker single) => _single = single;

    public ContainerPackResult Pack(IReadOnlyList<PackRequest> requests, double seconds)
    {
        var elapsed = Stopwatch.StartNew();
        var identity = new PackingIdentity(requests);
        IReadOnlyList<PackRequest> anonymous = identity.Requests;
        PackResult[] prepared = anonymous.Select(r =>
        {
            PackResult baseline = _single.Pack(r, 0);
            return CategoryPacking.Seed(r, baseline);
        }).ToArray();
        int[] compactable = Enumerable.Range(0, anonymous.Count).Where(i =>
            (_single is CpSatPacker || _single is CachedPacker)
            && prepared[i].Complete
            && anonymous[i].Items.All(item => item.Required)
            && CategoryOrder.Penalty(anonymous[i], prepared[i]) == 0
            && CpSatPacker.Height(anonymous[i], prepared[i]) > Array.FindIndex(
                CpSatPacker.AvailableCells(anonymous[i]),
                cells => cells >= CpSatPacker.Area(anonymous[i], prepared[i])))
            .ToArray();
        try
        {
            for (int index = 0; index < compactable.Length; index++)
            {
                double budget = Math.Min(MaxCompactionSeconds,
                    (seconds - elapsed.Elapsed.TotalSeconds)
                        / (compactable.Length - index + 1));
                if (budget <= 0) { break; }
                int grid = compactable[index];
                prepared[grid] = CategoryOrderCompaction.TryCompact(
                    anonymous[grid], prepared[grid], budget);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException
            || ex is TypeInitializationException
            || ex is System.IO.FileNotFoundException
            || ex is System.IO.FileLoadException)
        {
            // 与主求解相同的加载失败路径；已生成的完整提示仍可使用。
        }
        // 独立网格已经达到全部目标界限时，对联合最优值的贡献固定。
        int[] active = Enumerable.Range(0, anonymous.Count).Where(i =>
            !prepared[i].Unplaced.Any(item => item.Required)
            && !AtBound(anonymous[i], prepared[i])).ToArray();
        var baselineResult = new ContainerPackResult(prepared);
        double remaining = seconds - elapsed.Elapsed.TotalSeconds;
        if (active.Length == 0 || remaining <= 0
            || (_single is not CpSatPacker && _single is not CachedPacker))
        {
            return Finish(baselineResult, "skip=bound-or-budget");
        }
        PackRequest[] pending = active.Select(i => anonymous[i]).ToArray();
        var before = new ContainerPackResult(active.Select(i => prepared[i]).ToArray());
        try
        {
            ContainerPackResult? solved = CategoryPackingModel.Solve(
                pending, before, remaining, out string status);
            if (solved != null && Better(pending, solved, before))
            {
                for (int i = 0; i < active.Length; i++)
                    prepared[active[i]] = solved.Grids[i];
            }
            return Finish(baselineResult, status);
        }
        catch (Exception ex) when (ex is DllNotFoundException
            || ex is TypeInitializationException
            || ex is System.IO.FileNotFoundException
            || ex is System.IO.FileLoadException)
        {
            return Finish(baselineResult with
            {
                Warning = "全局求解器加载失败，已采用各网格保底布局",
            }, "solver unavailable: " + ex);
        }

        ContainerPackResult Finish(ContainerPackResult result, string status)
        {
            bool orderFailed = false;
            result = result with
            {
                Grids = result.Grids.Select((packed, grid) =>
                {
                    PackRequest request = anonymous[grid];
                    if (CategoryOrder.Penalty(request, packed) == 0)
                        return packed;
                    orderFailed = true;
                    var current = new HashSet<string>(
                        request.Current.Select(p => p.Id));
                    return new PackResult(request.Current,
                        request.Items.Where(i => !current.Contains(i.Id)).ToArray());
                }).ToArray(),
            };
            if (orderFailed)
            {
                result = result with
                {
                    Warning = "当前预算内未找到符合类别顺序的布局，相关网格保持原样",
                };
            }
            return identity.Restore(result with
            {
                Diagnostic = $"global {status}; grids={requests.Count}, "
                    + $"active={active.Length}, "
                    + $"elapsed={elapsed.ElapsedMilliseconds}ms",
            });
        }
    }

    private static bool AtBound(PackRequest request, PackResult result)
    {
        int area = CpSatPacker.Area(request, result);
        return result.Complete && area == CategoryPacking.AreaUpperBound(request)
            && CategoryOrder.Penalty(request, result) == 0
            && CpSatPacker.Height(request, result)
                == Array.FindIndex(CpSatPacker.AvailableCells(request), a => a >= area)
            && Enumerable.Range(0, CategoryPacking.Depth(request)).All(d =>
                CategoryPacking.Span(request, result, d)
                    == CategoryPacking.LowerBound(request, area, d))
            && CategoryPacking.CoordinatesAtBound(request, result, area);
    }

    private static bool Better(IReadOnlyList<PackRequest> requests,
        ContainerPackResult next, ContainerPackResult before)
    {
        if (next.Grids.Any(r => !r.Complete)) { return false; }
        return CategoryPacking.Compare(requests, next, before) < 0;
    }
}
