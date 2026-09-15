using System;
using System.Linq;
using ChouUn.StashMaster.Core.Packing;
using Xunit;

namespace ChouUn.StashMaster.Core.Tests.Packing;

public sealed class DirectionalPackingTests
{
    [Fact]
    public void 高度和聚合相同时先填上方空洞再靠左()
    {
        var request = new PackRequest(2, 2, Array.Empty<FixedBlock>(),
            new[] { "a", "b", "c" }.Select(id => new PackItem(id, "same", 1, 1)
            { Required = true, SortType = "Barter" }).ToArray())
        {
            Current = new[]
            {
                new Placement("a", 0, 0, false), new Placement("b", 0, 1, false),
                new Placement("c", 1, 1, false),
            },
        };
        var before = new PackResult(request.Current, Array.Empty<PackItem>());
        ContainerPackResult result = CategoryPackingModel.Solve(new[] { request },
            new ContainerPackResult(new[] { before }), 2, out _)!;
        CpSatPackerTests.AssertValid(request, result.Grids[0]);
        Assert.Equal(new[] { (0, 0), (1, 0), (0, 1) }, result.Grids[0].Placements
            .OrderBy(p => p.Y).ThenBy(p => p.X).Select(p => (p.X, p.Y)).ToArray());
        Assert.Equal(CategoryPacking.Spans(request, before),
            CategoryPacking.Spans(request, result.Grids[0]));
        Assert.True(CategoryPacking.Score(request, result.Grids[0]) < CategoryPacking.Score(request, before));
    }

    [Fact]
    public void 同分保留原位不能遗漏新布局能放下的必留物品()
    {
        var request = new PackRequest(3, 2, Array.Empty<FixedBlock>(), new[]
        {
            new PackItem("square", "same", 2, 2) { Required = true },
            new PackItem("bar", "same", 2, 1) { Required = true },
        }.Concat(Enumerable.Range(0, 4).Select(i =>
            new PackItem("single-" + i, "same", 1, 1))).ToArray())
        {
            Current = new[] { new Placement("bar", 1, 0, false) },
        };

        PackResult result = new CpSatPacker().Pack(request, 0);

        Assert.Contains(result.Placements, p => p.Id == "square");
        Assert.Contains(result.Placements, p => p.Id == "bar");
        Assert.DoesNotContain(result.Unplaced, i => i.Required);
        CpSatPackerTests.AssertValid(request, result);
    }

    [Fact]
    public void 转置与未选候选的坐标计分和实际占格一致()
    {
        var request = new PackRequest(3, 2, new[] { new FixedBlock(2, 0, 1, 2) }, new[]
        {
            new PackItem("bar", "bar", 1, 2) { Required = true, SortType = "Barter" },
            new PackItem("too-large", "large", 3, 3) { SortType = "Barter" },
        });
        var before = new PackResult(new[] { new Placement("bar", 0, 1, true) },
            new[] { request.Items[1] });
        ContainerPackResult result = CategoryPackingModel.Solve(new[] { request },
            new ContainerPackResult(new[] { before }), 2, out _)!;
        Placement bar = Assert.Single(result.Grids[0].Placements);
        Assert.Equal("bar", bar.Id);
        Assert.True(bar.Rotated);
        Assert.Equal((0, 0), (bar.X, bar.Y));
        Assert.Equal("too-large", Assert.Single(result.Grids[0].Unplaced).Id);
        Assert.Equal(0L, CategoryPacking.CoordinateSum(request, result.Grids[0], false));
        Assert.Equal(1L, CategoryPacking.CoordinateSum(request, result.Grids[0], true));
        Assert.True(CategoryPacking.Score(request, result.Grids[0]) < CategoryPacking.Score(request, before));
        CpSatPackerTests.AssertValid(request, result.Grids[0]);
    }
}
