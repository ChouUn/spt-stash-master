using System;
using System.Linq;
using ChouUn.StashMaster.Core.Packing;
using Xunit;

namespace ChouUn.StashMaster.Core.Tests.Packing;

public sealed class PackingIdentityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void 同类位置集合不变时保留全部真实物品(double seconds)
    {
        var request = Grid(4, 1, Item("a"), Item("b"), Item("c"), Item("d")) with
        {
            Current = new[]
            {
                new Placement("d", 0, 0, false), new Placement("b", 1, 0, false),
                new Placement("a", 2, 0, false), new Placement("c", 3, 0, false),
            },
        };

        PackResult result = new CpSatPacker().Pack(request, seconds);

        Assert.Equal(request.Current.OrderBy(p => p.Id),
            result.Placements.OrderBy(p => p.Id));
        CpSatPackerTests.AssertValid(request, result);
    }

    [Fact]
    public void 压紧之后优先把仍可保留的原位留给对应物品()
    {
        var request = Grid(3, 2, Item("a"), Item("b"), Item("c")) with
        {
            Current = new[]
            {
                new Placement("a", 2, 1, false), new Placement("b", 0, 0, false),
                new Placement("c", 1, 0, false),
            },
        };

        PackResult result = new CpSatPacker().Pack(request, 0);

        Assert.Contains(new Placement("b", 0, 0, false), result.Placements);
        Assert.Contains(new Placement("c", 1, 0, false), result.Placements);
        Assert.Contains(new Placement("a", 2, 0, false), result.Placements);
        CpSatPackerTests.AssertValid(request, result);
    }

    [Fact]
    public void 未选候选不能挤掉同类原位物品()
    {
        var request = Grid(1, 1, Item("a") with { Required = false },
            Item("b") with { Required = false }) with
        {
            Current = new[] { new Placement("b", 0, 0, false) },
        };

        PackResult result = new CpSatPacker().Pack(request, 0);

        Assert.Equal("b", Assert.Single(result.Placements).Id);
        Assert.Equal("a", Assert.Single(result.Unplaced).Id);
    }

    [Fact]
    public void 原位匹配包含转置且保留同模板身份()
    {
        var request = Grid(2, 3,
            Item("a") with { Width = 3 }, Item("b") with { Width = 3 }) with
        {
            Current = new[]
            {
                new Placement("b", 0, 0, true), new Placement("a", 1, 0, true),
            },
        };
        PackResult result = new CpSatPacker().Pack(request);
        Assert.Equal(request.Current.OrderBy(p => p.Id),
            result.Placements.OrderBy(p => p.Id));
        CpSatPackerTests.AssertValid(request, result);
    }

    [Fact]
    public void 同类候选只在允许进入的网格内映射且必留物品不被替代()
    {
        PackItem a = Item("a") with { Required = false };
        PackItem b = Item("b") with { Required = false };
        PackRequest[] requests =
        {
            Grid(2, 1, Item("keep"), a, b) with
            {
                Current = new[] { new Placement("keep", 1, 0, false) },
            },
            Grid(1, 1, b),
        };

        ContainerPackResult result = new ContainerPacker(new CpSatPacker())
            .Pack(requests, 1);

        Assert.Contains(new Placement("keep", 1, 0, false),
            result.Grids[0].Placements);
        Assert.Contains(result.Grids[0].Placements, p => p.Id == "a");
        Assert.Equal("b", Assert.Single(result.Grids[1].Placements).Id);
        for (int i = 0; i < requests.Length; i++)
            CpSatPackerTests.AssertValid(requests[i], result.Grids[i]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 等价物品身份重命名不改变位置集合(bool reverse)
    {
        string[] names = reverse ? new[] { "z", "x", "y" }
            : new[] { "a", "c", "b" };
        var request = Grid(3, 2, names.Select(id => Item(id)).ToArray()) with
        {
            Current = new[]
            {
                new Placement(names[0], 2, 1, false),
                new Placement(names[1], 0, 0, false),
                new Placement(names[2], 1, 0, false),
            },
        };

        PackResult result = new CpSatPacker().Pack(request);

        Assert.Contains(new Placement(names[1], 0, 0, false), result.Placements);
        Assert.Contains(new Placement(names[2], 1, 0, false), result.Placements);
        Assert.Contains(new Placement(names[0], 2, 0, false), result.Placements);
    }

    [Fact]
    public void 同形同模板但父类不同不能混合身份()
    {
        var request = Grid(2, 2, Item("a"), Item("b"),
            Item("c") with { SortType = "other", SubcategoryPath = new[] { "other-child" } },
            Item("d") with { SortType = "other", SubcategoryPath = new[] { "other-child" } }) with
        {
            Current = new[]
            {
                new Placement("a", 0, 0, false), new Placement("b", 0, 1, false),
                new Placement("c", 1, 0, false), new Placement("d", 1, 1, false),
            },
        };

        PackResult result = new CpSatPacker().Pack(request);

        Assert.Equal(new[] { 0 }, CategoryPacking.Spans(request, result));
        Assert.Equal(2, result.Placements.Count(p => request.Current.Contains(p)));
        CpSatPackerTests.AssertValid(request, result);
    }

    [Fact]
    public void 跨网格同类候选映射保持各自原位()
    {
        PackItem[] items = new[] { Item("a"), Item("b") }
            .Select(i => i with { Required = false }).ToArray();
        PackRequest[] requests =
        {
            Grid(1, 1, items) with
            {
                Current = new[] { new Placement("b", 0, 0, false) },
            },
            Grid(1, 1, items) with
            {
                Current = new[] { new Placement("a", 0, 0, false) },
            },
        };

        ContainerPackResult result = new ContainerPacker(new CpSatPacker())
            .Pack(requests, 1);

        Assert.Equal("b", Assert.Single(result.Grids[0].Placements).Id);
        Assert.Equal("a", Assert.Single(result.Grids[1].Placements).Id);
        for (int grid = 0; grid < requests.Length; grid++)
            CpSatPackerTests.AssertValid(requests[grid], result.Grids[grid]);
    }

    private static PackItem Item(string id) => new(id, "same", 1, 1)
    {
        Required = true,
        SortType = "parent",
        SubcategoryPath = new[] { "child" },
    };

    private static PackRequest Grid(int width, int height, params PackItem[] items)
        => new(width, height, Array.Empty<FixedBlock>(), items);
}
