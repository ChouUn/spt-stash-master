using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Google.OrTools.Sat;

namespace ChouUn.StashMaster.Core.Packing;

/// <summary>各网格内部加权空间与类别跨度，再累加分数共同求解。</summary>
internal static class CategoryPackingModel
{
    public static ContainerPackResult? Solve(IReadOnlyList<PackRequest> requests,
        ContainerPackResult baseline, double seconds, out string status)
    {
        var elapsed = Stopwatch.StartNew();
        // 固定占位后的网格互不耦合；先处理小网格，避免大仓库建模耗尽提示预算。
        // 占位提示与联合几何按 2:1 分配剩余预算；提示未用完的额度交给几何搜索。
        var diagnostics = new List<string>();
        PackResult[] prepared = baseline.Grids.ToArray();
        int[] eligible = Enumerable.Range(0, requests.Count).Where(i =>
            prepared[i].Complete && requests[i].Items.All(item => item.Required)
            && CategoryOrder.Penalty(requests[i], prepared[i]) == 0)
            .OrderBy(i => requests[i].Items.Count).ToArray();
        for (int index = 0; index < eligible.Length; index++)
        {
            double budget = (seconds * (2.0 / 3) - elapsed.Elapsed.TotalSeconds)
                / (eligible.Length - index);
            if (budget <= 0) { break; }
            int grid = eligible[index];
            var request = new[] { requests[grid] };
            var before = new ContainerPackResult(new[] { prepared[grid] });
            ContainerPackResult reassigned = CategorySlotModel.Solve(request, before,
                budget, out string reassignmentStatus);
            diagnostics.Add($"slots[{grid}]={reassignmentStatus}");
            if (CategoryPacking.Compare(request, reassigned, before) < 0)
                prepared[grid] = reassigned.Grids[0];
        }
        baseline = new ContainerPackResult(prepared);
        string prefix = diagnostics.Count == 0 ? "" : string.Join("; ", diagnostics) + "; ";
        double remaining = seconds - elapsed.Elapsed.TotalSeconds;
        if (remaining <= 0)
        {
            status = prefix + "budget-exhausted";
            return baseline;
        }
        ContainerPackResult? result = SolveModel(requests, baseline, remaining,
            out string globalStatus);
        status = prefix + globalStatus;
        return result ?? baseline;
    }

    private static ContainerPackResult? SolveModel(IReadOnlyList<PackRequest> requests,
        ContainerPackResult baseline, double seconds, out string status)
    {
        var elapsed = Stopwatch.StartNew();
        var model = new CpModel();
        var grids = new List<List<CpSatModel.Rectangle>>();
        var objectives = new List<IReadOnlyList<PackingObjective>>();
        var details = new List<string>();
        var layoutHints = new ContainerPackResult(requests.Select((r, i) =>
            CategoryOrder.Penalty(r, baseline.Grids[i]) == 0
                ? baseline.Grids[i] : CategoryOrder.Hint(r, baseline.Grids[i]))
            .ToArray());
        for (int grid = 0; grid < requests.Count; grid++)
        {
            PackRequest request = requests[grid];
            PackResult before = baseline.Grids[grid];
            PackingTree tree = PackingTree.For(request);
            NoOverlap2dConstraint overlap = model.AddNoOverlap2D();
            foreach (FixedBlock block in request.Fixed)
            {
                overlap.AddRectangle(model.NewFixedSizeIntervalVar(
                    LinearExpr.Constant(block.X), block.Width, "fixed-x"),
                    model.NewFixedSizeIntervalVar(
                        LinearExpr.Constant(block.Y), block.Height, "fixed-y"));
            }
            IntVar top = model.NewIntVar(0, request.Height, "top-" + grid);
            var rectangles = new List<CpSatModel.Rectangle>();
            grids.Add(rectangles);
            PackResult layoutHint = layoutHints.Grids[grid];
            var hints = layoutHint.Placements.ToDictionary(p => p.Id);
            foreach (PackItem item in request.Items)
            {
                CpSatModel.Rectangle rect =
                    CpSatModel.AddItem(model, overlap, request, item, top);
                rectangles.Add(rect);
                bool present = hints.TryGetValue(item.Id, out Placement? p);
                model.AddHint(rect.Present, present);
                model.AddHint(rect.X, present ? p!.X : 0);
                model.AddHint(rect.Y, present ? p!.Y : 0);
                model.AddHint(rect.Rotated, present && p!.Rotated);
                model.AddHint(rect.EndX, present
                    ? p!.X + (p.Rotated ? item.Height : item.Width) : 0);
                model.AddHint(rect.EndY, present
                    ? p!.Y + (p.Rotated ? item.Width : item.Height) : 0);
            }
            LinearExpr area = LinearExpr.Sum(rectangles.Select(r =>
                r.Present * (r.Item.Width * r.Item.Height)));
            IntVar available = model.NewIntVar(0,
                request.Width * request.Height, "available");
            long[] cells = CpSatPacker.AvailableCells(request);
            model.AddElement(top, cells, available);
            model.Add(area <= available);
            int height = CpSatPacker.Height(request, layoutHint);
            model.AddHint(top, height);
            model.AddHint(available, cells[height]);
            IReadOnlyDictionary<PackingTree.Node, CategoryRangeModel.Range> ranges =
                CategoryRangeModel.AddGeometry(model, request, tree.Nodes, rectangles, hints);
            CategoryOrderModel.Add(model, request, ranges);
            objectives.Add(GridObjectives(model, request, before, top, area,
                tree, ranges, rectangles, out string detail));
            details.Add($"grid={grid} {detail}");
        }
        foreach (var item in grids.SelectMany(g => g).GroupBy(r => r.Item.Id))
        {
            model.Add(LinearExpr.Sum(item.Select(r => r.Present)) <= 1);
        }

        PackingSymmetry.Add(model, requests, grids.SelectMany((g, index) =>
            g.Select(r => (index, r))), layoutHints);
        bool orderedBaseline = requests.Select((r, i) =>
            CategoryOrder.Penalty(r, baseline.Grids[i]) == 0).All(x => x);
        IReadOnlyList<PackingObjective> blocks =
            PackingObjective.Sum(model, objectives);
        var diagnostics = new List<string>();
        if (blocks.Count == 0)
        {
            status = "Optimal; objective=grid-sum; " + string.Join("; ", details);
            return baseline;
        }
        using var solver = new CpSolver();
        using var progress = new PackingProgress(requests, baseline,
            callback => ReadResult(variable => callback.Value(variable)),
            solver.StopSearch);
        bool hasSolution = false;
        status = "Optimal";
        for (int stage = 0; stage < blocks.Count; stage++)
        {
            PackingObjective block = blocks[stage];
            double remaining = seconds - elapsed.Elapsed.TotalSeconds;
            if (remaining <= 0)
            {
                status = "Feasible; budget-exhausted before " + block.Label;
                break;
            }
            LinearExpr objective = block.Expression;
            long ceiling = hasSolution ? solver.Value(objective) : block.Current;
            if (hasSolution || orderedBaseline) { model.Add(objective <= ceiling); }
            model.Minimize(objective);
            var stageClock = Stopwatch.StartNew();
            solver.StringParameters = "max_time_in_seconds:"
                + remaining.ToString("R", CultureInfo.InvariantCulture)
                + ",num_workers:1,random_seed:0";
            CpSolverStatus solved;
            try
            {
                solved = solver.Solve(model, progress.Callback);
            }
            finally
            {
                progress.EndSearch();
            }
            diagnostics.Add($"block={block.Label}:{solved}:"
                + $"{stageClock.ElapsedMilliseconds}ms");
            status = solved.ToString() + "; objective=grid-sum; "
                + string.Join("; ", diagnostics) + "; " + progress.Diagnostic
                + "; " + string.Join("; ", details);
            if (solved != CpSolverStatus.Optimal && solved != CpSolverStatus.Feasible)
            {
                return hasSolution ? progress.Best : null;
            }
            hasSolution = true;
            // 下一阶段沿用刚求出的完整可行解，不重新猜测物品位置。
            model.ClearHints();
            model.Model.SolutionHint = new PartialVariableAssignment();
            // 上面的 Feasible/Optimal 状态保证存在求解响应。
            CpSolverResponse response = solver.Response!;
            for (int variable = 0; variable < response.Solution.Count; variable++)
            {
                model.Model.SolutionHint.Vars.Add(variable);
                model.Model.SolutionHint.Values.Add(response.Solution[variable]);
            }
            if (solved != CpSolverStatus.Optimal || progress.Stopped)
            {
                break;
            }
            model.Add(objective == solver.Value(objective));
        }
        return hasSolution ? progress.Best : null;

        ContainerPackResult ReadResult(Func<IntVar, long> value)
        {
            var results = new List<PackResult>();
            for (int grid = 0; grid < requests.Count; grid++)
            {
                Placement[] placements = grids[grid]
                    .Where(r => value(r.Present) != 0)
                    .Select(r => new Placement(r.Item.Id, (int)value(r.X),
                        (int)value(r.Y), value(r.Rotated) != 0))
                    .ToArray();
                var ids = new HashSet<string>(placements.Select(p => p.Id));
                results.Add(new PackResult(placements,
                    requests[grid].Items.Where(i => !ids.Contains(i.Id)).ToArray()));
            }
            return new ContainerPackResult(results);
        }
    }

    /// <summary>仅在本网格内确定优先级；固定项省去常数，倍率仍按原上界计算。</summary>
    private static IReadOnlyList<PackingObjective> GridObjectives(CpModel model,
        PackRequest request, PackResult before, IntVar top, LinearExpr area,
        PackingTree tree,
        IReadOnlyDictionary<PackingTree.Node, CategoryRangeModel.Range> ranges,
        IReadOnlyList<CpSatModel.Rectangle> rectangles,
        out string detail)
    {
        int areaBefore = CpSatPacker.Area(request, before);
        int heightBefore = CpSatPacker.Height(request, before);
        int areaUpper = CategoryPacking.AreaUpperBound(request);
        bool allRequired = request.Items.All(i => i.Required);
        long heightWeight = request.Height + 1L;
        LinearExpr space = allRequired ? top
            : top + (areaUpper - area) * heightWeight;
        long spaceBefore = heightBefore + (allRequired ? 0
            : (areaUpper - areaBefore) * heightWeight);
        int lowerHeight = Array.FindIndex(CpSatPacker.AvailableCells(request),
            a => a >= areaBefore);
        bool spaceOptimal = areaBefore == areaUpper && heightBefore == lowerHeight
            && CategoryOrder.Penalty(request, before) == 0;
        if (spaceOptimal) { model.Add(space == spaceBefore); }
        var objectives = new List<PackingObjective>
        {
            new(space, request.Height + (allRequired ? 0 : areaUpper * heightWeight),
                spaceBefore, "space", spaceOptimal),
        };
        var notes = new List<string>();
        bool precedingFixed = spaceOptimal;
        for (int level = 0; level < tree.Levels.Count; level++)
        {
            string label = level.ToString(CultureInfo.InvariantCulture);
            LinearExpr expression = LinearExpr.Sum(tree.Levels[level]
                .Select(node => ranges[node].Span));
            long current = CategoryPacking.Span(request, before, level);
            long upper = CategoryPacking.UpperBound(request, level);
            long lower = spaceOptimal
                ? CategoryPacking.LowerBound(request, areaBefore, level) : 0;
            model.Add(expression >= lower);
            bool fixedValue = upper == lower || (current == lower && precedingFixed);
            if (fixedValue)
            {
                model.Add(expression == current);
                notes.Add(label + "=bound");
            }
            objectives.Add(new PackingObjective(expression, upper, current,
                label, fixedValue));
            precedingFixed &= fixedValue;
        }
        AddCoordinates(false);
        AddCoordinates(true);

        void AddCoordinates(bool horizontal)
        {
            string label = horizontal ? "columns" : "rows";
            long upper = CategoryPacking.CoordinateUpperBound(request, horizontal);
            long current = CategoryPacking.CoordinateSum(request, before, horizontal);
            var terms = new List<LinearExpr>();
            foreach (CpSatModel.Rectangle rect in rectangles)
            {
                PackItem item = rect.Item;
                long itemArea = (long)item.Width * item.Height;
                int extent = horizontal ? item.Width : item.Height;
                int rotatedExtent = horizontal ? item.Height : item.Width;
                LinearExpr moment = itemArea * (horizontal ? rect.X : rect.Y)
                    + itemArea * (extent - 1) / 2
                    + rect.Rotated * (itemArea * (rotatedExtent - extent) / 2);
                if (item.Required)
                {
                    terms.Add(moment);
                }
                else
                {
                    IntVar selected = model.NewIntVar(0, upper, label + "-selected");
                    model.Add(selected == moment).OnlyEnforceIf(rect.Present);
                    model.Add(selected == 0).OnlyEnforceIf(rect.Present.Not());
                    terms.Add(selected);
                }
            }
            LinearExpr expression = LinearExpr.Sum(terms);
            long lower = CategoryPacking.CoordinateLowerBound(request, areaBefore, horizontal);
            // 只有面积固定后才可以用该面积的坐标下界约束模型。
            if (spaceOptimal || allRequired) { model.Add(expression >= lower); }
            bool fixedValue = precedingFixed && current == lower;
            if (fixedValue) { model.Add(expression == current); }
            objectives.Add(new PackingObjective(expression, upper, current, label, fixedValue));
            precedingFixed &= fixedValue;
        }
        detail = "layers=" + tree.Levels.Count
            + (notes.Count == 0 ? "" : ", " + string.Join(",", notes));
        return objectives;
    }
}
