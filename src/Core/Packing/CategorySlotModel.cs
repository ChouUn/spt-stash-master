using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Google.OrTools.Sat;

namespace ChouUn.StashMaster.Core.Packing;

/// <summary>按类别和行分配等尺寸占位，不枚举物品身份的排列；占用几何不变。</summary>
internal static class CategorySlotModel
{
    private sealed record Allocation(PackItem[] Items, Placement[] Slots,
        IntVar Count);

    public static ContainerPackResult Solve(IReadOnlyList<PackRequest> requests,
        ContainerPackResult baseline, double seconds, out string status)
    {
        var clock = Stopwatch.StartNew();
        var model = new CpModel();
        var plans = new List<List<Allocation>>();
        var originals = new List<Dictionary<string, Placement>>();
        var objectives = new List<IReadOnlyList<PackingObjective>>();
        for (int grid = 0; grid < requests.Count; grid++)
        {
            PackRequest request = requests[grid];
            PackResult before = baseline.Grids[grid];
            PackingTree tree = PackingTree.For(request);
            var sources = tree.Nodes.Select(_ => new List<CategoryRangeModel.Member>()).ToArray();
            var items = request.Items.ToDictionary(i => i.Id);
            var positions = before.Placements.ToDictionary(p => p.Id);
            originals.Add(positions);
            var allocations = new List<Allocation>();
            plans.Add(allocations);
            // 同池占位互不重叠且转置后尺寸相同，任意重分配均保持几何与固定障碍不变。
            foreach (var pool in before.Placements.GroupBy(p => (
                Width: p.Rotated ? items[p.Id].Height : items[p.Id].Width,
                Height: p.Rotated ? items[p.Id].Width : items[p.Id].Height)))
            {
                Placement[][] rows = pool.GroupBy(p => p.Y).OrderBy(g => g.Key)
                    .Select(g => g.OrderBy(p => p.X).ToArray()).ToArray();
                var capacity = rows.Select(_ => new List<IntVar>()).ToArray();
                foreach (var group in pool.Select(p => items[p.Id]).GroupBy(i =>
                {
                    IReadOnlyList<PackingTree.Node> path = tree.Paths[i.Id];
                    return path[path.Count - 1];
                }))
                {
                    PackItem[] members = group.OrderBy(i => i.TemplateId, StringComparer.Ordinal)
                        .ThenBy(i => i.Id, StringComparer.Ordinal).ToArray();
                    var counts = new List<IntVar>();
                    for (int row = 0; row < rows.Length; row++)
                    {
                        Placement[] slots = rows[row];
                        int y = slots[0].Y;
                        IntVar count = model.NewIntVar(0,
                            Math.Min(members.Length, slots.Length), "slot-count");
                        BoolVar active = model.NewBoolVar("slot-active");
                        model.Add(count >= 1).OnlyEnforceIf(active);
                        model.Add(count == 0).OnlyEnforceIf(active.Not());
                        IntVar first = model.NewIntVar(0, request.Height, "slot-first");
                        IntVar end = model.NewIntVar(0, request.Height, "slot-end");
                        model.Add(first == y).OnlyEnforceIf(active);
                        model.Add(first == request.Height).OnlyEnforceIf(active.Not());
                        model.Add(end == y + pool.Key.Height).OnlyEnforceIf(active);
                        model.Add(end == 0).OnlyEnforceIf(active.Not());
                        int hint = members.Count(i => positions[i.Id].Y == y);
                        model.AddHint(count, hint);
                        model.AddHint(active, hint != 0);
                        model.AddHint(first, hint != 0 ? y : request.Height);
                        model.AddHint(end, hint != 0 ? y + pool.Key.Height : 0);
                        allocations.Add(new Allocation(members, slots, count));
                        var extent = new CategoryRangeModel.Member(first, end);
                        sources[group.Key.Index].Add(extent);
                        capacity[row].Add(count);
                        counts.Add(count);
                    }
                    model.Add(LinearExpr.Sum(counts) == members.Length);
                }
                for (int row = 0; row < rows.Length; row++)
                    model.Add(LinearExpr.Sum(capacity[row]) == rows[row].Length);
            }
            var ranges = CategoryRangeModel.Add(model, request, tree.Nodes,
                node => sources[node.Index], positions, allPresent: true);
            var goals = new List<PackingObjective>
            {
                new(model.NewConstant(CpSatPacker.Height(request, before)), request.Height,
                    CpSatPacker.Height(request, before), "space", true),
            };
            for (int level = 0; level < tree.Levels.Count; level++)
            {
                LinearExpr spans = LinearExpr.Sum(tree.Levels[level]
                    .Select(node => ranges[node].Span));
                goals.Add(new PackingObjective(spans,
                    CategoryPacking.UpperBound(request, level),
                    CategoryPacking.Span(request, before, level), "category-" + level));
            }
            // 固定占位的坐标贡献不变；仍保留跨网格完整计分所需的倍率。
            foreach (bool horizontal in new[] { false, true })
            {
                long coordinate = CategoryPacking.CoordinateSum(request, before, horizontal);
                goals.Add(new PackingObjective(LinearExpr.Constant(coordinate),
                    CategoryPacking.CoordinateUpperBound(request, horizontal), coordinate,
                    horizontal ? "columns" : "rows", true));
            }
            CategoryOrderModel.Add(model, request, ranges);
            objectives.Add(goals);
        }
        using var solver = new CpSolver();
        using var progress = new PackingProgress(requests, baseline,
            callback => Read(variable => callback.Value(variable)), solver.StopSearch);
        status = "Optimal";
        IReadOnlyList<PackingObjective> blocks = PackingObjective.Sum(model, objectives);
        for (int stage = 0; stage < blocks.Count; stage++)
        {
            PackingObjective objective = blocks[stage];
            double remaining = seconds - clock.Elapsed.TotalSeconds;
            if (remaining <= 0) { status = "budget-exhausted"; break; }
            model.Minimize(objective.Expression);
            solver.StringParameters = "max_time_in_seconds:"
                + remaining.ToString("R", CultureInfo.InvariantCulture)
                + ",num_workers:" + Math.Min(8, Environment.ProcessorCount)
                + ",random_seed:0";
            CpSolverStatus solved;
            try { solved = solver.Solve(model, progress.Callback); }
            finally { progress.EndSearch(); }
            status = solved + "; " + progress.Diagnostic;
            if (solved != CpSolverStatus.Optimal || progress.Stopped) { break; }
            model.Add(objective.Expression == solver.Value(objective.Expression));
            if (stage + 1 == blocks.Count) { break; }
            // 下一段使用满足刚锁定目标的完整解，避免修复原始布局的过时提示。
            model.ClearHints();
            model.Model.SolutionHint = new PartialVariableAssignment();
            CpSolverResponse response = solver.Response!;
            for (int variable = 0; variable < response.Solution.Count; variable++)
            {
                model.Model.SolutionHint.Vars.Add(variable);
                model.Model.SolutionHint.Values.Add(response.Solution[variable]);
            }
        }
        return progress.Best;

        ContainerPackResult Read(Func<IntVar, long> value)
        {
            var results = new List<PackResult>();
            for (int grid = 0; grid < plans.Count; grid++)
            {
                var placements = new List<Placement>();
                var offsets = new Dictionary<PackItem[], int>();
                var occupied = new Dictionary<Placement[], int>();
                foreach (Allocation allocation in plans[grid])
                {
                    offsets.TryGetValue(allocation.Items, out int itemIndex);
                    occupied.TryGetValue(allocation.Slots, out int slotIndex);
                    int count = (int)value(allocation.Count);
                    for (int i = 0; i < count; i++)
                    {
                        PackItem item = allocation.Items[itemIndex++];
                        Placement slot = allocation.Slots[slotIndex++];
                        placements.Add(new Placement(item.Id, slot.X, slot.Y,
                            originals[grid][item.Id].Rotated));
                    }
                    offsets[allocation.Items] = itemIndex;
                    occupied[allocation.Slots] = slotIndex;
                }
                results.Add(new PackResult(placements, Array.Empty<PackItem>()));
            }
            return new ContainerPackResult(results);
        }
    }
}
