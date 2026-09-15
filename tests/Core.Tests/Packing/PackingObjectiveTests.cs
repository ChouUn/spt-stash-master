using System;
using System.Collections.Generic;
using System.Linq;
using ChouUn.StashMaster.Core.Packing;
using Google.OrTools.Sat;
using Xunit;

namespace ChouUn.StashMaster.Core.Tests.Packing;

public sealed class PackingObjectiveTests
{
    [Fact]
    public void 跨网格收益按各自权重计分()
    {
        var model = new CpModel();
        IntVar parent = model.NewIntVar(0, 1, "a-parent");
        IntVar child = model.NewIntVar(0, 1000, "b-child");
        model.Add(child == 1000 - parent * 1000);
        IReadOnlyList<PackingObjective>[] grids =
        {
            new[]
            {
                new PackingObjective(parent, 1, 0, "parent"),
                new PackingObjective(LinearExpr.Constant(0), 1, 0, "child", true),
            },
            new[] { new PackingObjective(child, 1000, 1000, "child") },
        };
        PackingObjective total = Assert.Single(PackingObjective.Sum(model, grids));
        model.Minimize(total.Expression);
        using var solver = new CpSolver();

        Assert.Equal(CpSolverStatus.Optimal, solver.Solve(model));
        Assert.Equal(1, solver.Value(parent));
        Assert.Equal(0, solver.Value(child));
        Assert.Equal(2, solver.Value(total.Expression));
    }

    [Fact]
    public void 固定子层仍保留本网格父层的倍率()
    {
        var model = new CpModel();
        IntVar parent = model.NewIntVar(0, 1, "parent");
        IntVar other = model.NewIntVar(0, 4, "other");
        model.Add(other == 4 - parent * 4);
        IReadOnlyList<PackingObjective>[] grids =
        {
            new[]
            {
                new PackingObjective(parent, 1, 0, "parent"),
                new PackingObjective(LinearExpr.Constant(0), 9, 0, "fixed", true),
            },
            new[] { new PackingObjective(other, 4, 4, "other") },
        };
        PackingObjective total = Assert.Single(PackingObjective.Sum(model, grids));
        model.Minimize(total.Expression);
        using var solver = new CpSolver();

        Assert.Equal(CpSolverStatus.Optimal, solver.Solve(model));
        Assert.Equal(0, solver.Value(parent));
        Assert.Equal(4, solver.Value(total.Expression));
    }

    [Fact]
    public void 四层超大分数先跨网格相加_仍能分辨总分差一()
    {
        var model = new CpModel();
        BoolVar choice = model.NewBoolVar("choice");
        const long upper = 1L << 30;
        IReadOnlyList<PackingObjective>[] grids = Enumerable.Range(0, 2)
            .Select(grid => (IReadOnlyList<PackingObjective>)new[]
            {
                new PackingObjective(grid == 0 ? 1 - choice : choice,
                    upper, grid, "0"),
                new PackingObjective(LinearExpr.Constant(0), upper, 0, "1"),
                new PackingObjective(LinearExpr.Constant(0), upper, 0, "2"),
                new PackingObjective(grid == 0 ? choice : LinearExpr.Constant(0),
                    upper, grid == 0 ? 1 : 0, "3"),
            }).ToArray();
        IReadOnlyList<PackingObjective> blocks = PackingObjective.Sum(model, grids);
        using var solver = new CpSolver();
        foreach (PackingObjective block in blocks)
        {
            model.Minimize(block.Expression);
            Assert.Equal(CpSolverStatus.Optimal, solver.Solve(model));
            model.Add(block.Expression == solver.Value(block.Expression));
        }

        // 总分恒定的大数 + choice；不能被分段重新解释成先优化某个网格。
        Assert.Equal(0, solver.Value(choice));
    }

    [Fact]
    public void 父类改善一单位压过子类全部范围()
    {
        var model = new CpModel();
        IntVar parent = model.NewIntVar(0, 1, "parent");
        IntVar child = model.NewIntVar(0, 1000, "child");
        model.Add(child >= 1000 - parent * 1000);
        PackingObjective combined = Assert.Single(PackingObjective.Combine(new[]
        {
            new PackingObjective(parent, 1, 1, "parent"),
            new PackingObjective(child, 1000, 0, "child"),
        }));
        model.Minimize(combined.Expression);
        using var solver = new CpSolver();

        Assert.Equal(CpSolverStatus.Optimal, solver.Solve(model));
        Assert.Equal(0, solver.Value(parent));
        Assert.Equal(1000, solver.Value(child));
    }


    [Fact]
    public void 同成员类别链压缩后仍按转置覆盖高度计分()
    {
        var request = new PackRequest(5, 10, Array.Empty<FixedBlock>(), new[]
        {
            new PackItem("a", "same", 6, 2)
            {
                Required = true, SortType = "Weapons",
                SubcategoryPath = new[] { "branch", "leaf" },
            },
            new PackItem("b", "same", 6, 2)
            {
                Required = true, SortType = "Weapons",
                SubcategoryPath = new[] { "branch", "leaf" },
            },
        });

        PackResult result = new CpSatPacker().Pack(request);

        Assert.Equal(new[] { 5 }, CategoryPacking.Spans(request, result));
        CpSatPackerTests.AssertValid(request, result);
    }
}
