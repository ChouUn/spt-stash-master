using System;
using System.Linq;
using ChouUn.StashMaster.Core.Packing;
using Xunit;

namespace ChouUn.StashMaster.Core.Tests.Packing;

public sealed class CategorySlotModelTests
{
    [Fact]
    public void 同子类可跨三行以上重分配_保持父层目标和原有占位()
    {
        PackItem[] items = Enumerable.Range(0, 16).Select(i =>
            new PackItem("item-" + i, "template-" + i % 2, 1, 1)
            {
                Required = true,
                SortType = "Valuables",
                SubcategoryPath = new[] { i % 2 == 0 ? "currency" : "jewelry" },
            }).ToArray();
        var request = new PackRequest(2, 8, Array.Empty<FixedBlock>(), items);
        var before = new PackResult(items.Select((item, i) =>
            new Placement(item.Id, i % 2, i / 2, false)).ToArray(), Array.Empty<PackItem>());
        var baseline = new ContainerPackResult(new[] { before });

        ContainerPackResult result = CategorySlotModel.Solve(new[] { request }, baseline, 5, out _);

        PackResult after = Assert.Single(result.Grids);
        Assert.Equal(new[] { 7, 14 }, CategoryPacking.Spans(request, before));
        Assert.Equal(new[] { 7, 6 }, CategoryPacking.Spans(request, after));
        Assert.Contains(after.Placements, p => Math.Abs(p.Y
            - before.Placements.Single(original => original.Id == p.Id).Y) > 3);
        AssertPreserved(request, before, after);
    }

    [Fact]
    public void 原始尺寸不同但旋转后占位相同的物品可交换_固定障碍不能被覆盖()
    {
        var items = new[]
        {
            Item("a-horizontal", "a", 2, 1),
            Item("b-horizontal", "b", 2, 1),
            Item("b-rotated", "b", 1, 2),
            Item("a-rotated", "a", 1, 2),
        };
        var request = new PackRequest(2, 5, new[] { new FixedBlock(0, 2, 2, 1) }, items);
        var before = new PackResult(new[]
        {
            new Placement("a-horizontal", 0, 0, false),
            new Placement("b-horizontal", 0, 1, false),
            new Placement("b-rotated", 0, 3, true),
            new Placement("a-rotated", 0, 4, true),
        }, Array.Empty<PackItem>());
        var baseline = new ContainerPackResult(new[] { before });

        ContainerPackResult result = CategorySlotModel.Solve(new[] { request }, baseline, 5, out _);

        PackResult after = Assert.Single(result.Grids);
        Assert.Equal(6, CategoryPacking.Span(request, before));
        Assert.Equal(2, CategoryPacking.Span(request, after));
        Assert.Contains(after.Placements, p => p.Rotated != before.Placements
            .Single(original => original.X == p.X && original.Y == p.Y).Rotated);
        AssertPreserved(request, before, after);
    }

    [Fact]
    public void 多网格各自聚合同类且保留所属物品_分别遵守相反的类别顺序()
    {
        PackRequest[] requests = Enumerable.Range(0, 2).Select(grid =>
        {
            PackItem[] items = Enumerable.Range(0, 8).Select(i =>
                new PackItem("grid-" + grid + "-item-" + i, "template-" + i, 1, 1)
                {
                    Required = true,
                    SortType = i % 2 == 0 ? "Armor" : "Ammo",
                }).ToArray();
            return new PackRequest(2, 4, Array.Empty<FixedBlock>(), items)
            {
                CategoryOrder = grid == 0 ? new[] { "Armor", "Ammo" } : new[] { "Ammo", "Armor" },
                Current = items.Select((item, i) =>
                    new Placement(item.Id, i % 2, i / 2, false)).ToArray(),
            };
        }).ToArray();
        var baseline = new ContainerPackResult(requests.Select(request =>
            new PackResult(request.Current, Array.Empty<PackItem>())).ToArray());

        ContainerPackResult result = CategorySlotModel.Solve(requests, baseline, 5, out _);

        Assert.Equal(requests.Length, result.Grids.Count);
        for (int grid = 0; grid < requests.Length; grid++)
        {
            PackRequest request = requests[grid];
            PackResult before = baseline.Grids[grid];
            PackResult after = result.Grids[grid];
            Assert.Equal(0, CategoryOrder.Penalty(request, before));
            Assert.Equal(6, CategoryPacking.Span(request, before));
            Assert.Equal(2, CategoryPacking.Span(request, after));
            Assert.Equal(0, CategoryOrder.Penalty(request, after));
            AssertPreserved(request, before, after);
        }
    }

    [Fact]
    public void 跨尺寸池共同计算类别范围_子层收益不能抵消顶层退步()
    {
        int[] categories = { 0, 0, 1, 1, 2, 3 };
        PackItem[] items = categories.Select((category, i) =>
            new PackItem("item-" + i, "template-" + category, i < 3 ? 2 : 1, 1)
            {
                Required = true,
                SortType = "parent-" + category / 2,
                SubcategoryPath = new[] { "child-" + category },
            }).ToArray();
        int[] rows = { 0, 1, 3, 0, 2, 3 };
        var request = new PackRequest(3, 4, Array.Empty<FixedBlock>(), items);
        var before = new PackResult(items.Select((item, i) =>
            new Placement(item.Id, i < 3 ? 0 : 2, rows[i], false)).ToArray(),
            Array.Empty<PackItem>());

        ContainerPackResult result = CategorySlotModel.Solve(new[] { request },
            new ContainerPackResult(new[] { before }), 5, out _);

        PackResult after = Assert.Single(result.Grids);
        // 子层跨度可降至 1，但须把父层跨度从 4 增至 5，不能接受该取舍。
        Assert.Equal(new[] { 4, 2 }, CategoryPacking.Spans(request, after));
        AssertPreserved(request, before, after);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 同名子类不跨排序顶层计分_顺序与聚合使用同一范围(bool slots)
    {
        PackItem[] items = Enumerable.Range(0, 8).Select(i =>
            new PackItem("item-" + i, "template-" + i, 1, 1)
            {
                Required = true,
                SubcategoryPath = new[] { "shared" },
                SortType = i % 2 != 0 ? "Middle" : i < 4 ? "First" : "Last",
            }).ToArray();
        var request = new PackRequest(2, 4, Array.Empty<FixedBlock>(), items)
        {
            CategoryOrder = new[] { "First", "Middle", "Last" },
        };
        var before = new PackResult(items.Select((item, i) =>
            new Placement(item.Id, i % 2, i / 2, false)).ToArray(), Array.Empty<PackItem>());

        var baseline = new ContainerPackResult(new[] { before });
        ContainerPackResult? result = slots
            ? CategorySlotModel.Solve(new[] { request }, baseline, 5, out _)
            : CategoryPackingModel.Solve(new[] { request }, baseline, 5, out _);
        Assert.NotNull(result);

        PackResult after = Assert.Single(result!.Grids);
        Assert.Equal(5, CategoryPacking.Span(request, before));
        // First 和 Last 各占一行，Middle 占两行；同名子类不产生跨类型共同跨度。
        Assert.Equal(1, CategoryPacking.Span(request, after));
        Assert.Equal(0, CategoryOrder.Penalty(request, after));
        AssertPreserved(request, before, after);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void 同类别不同模板按行聚合_不能作为跨模板等价身份互换(int solver)
    {
        PackItem[] items = Enumerable.Range(0, 8).Select(i =>
            new PackItem("item-" + i, "template-" + i % 2, 1, 1)
            {
                // 几何入口使用可选物品绕过占位预处理，守恒断言仍要求保留全部物品。
                Required = solver != 1,
                SortType = "Barter",
                SubcategoryPath = new[] { "common", "leaf" },
            }).ToArray();
        var request = new PackRequest(2, 4, Array.Empty<FixedBlock>(), items)
        {
            Current = items.Select((item, i) =>
                new Placement(item.Id, i % 2, i / 2, false)).ToArray(),
        };
        var before = new PackResult(request.Current, Array.Empty<PackItem>());
        var baseline = new ContainerPackResult(new[] { before });

        PackResult after;
        if (solver == 2)
        {
            after = new CpSatPacker().Pack(request, 5);
        }
        else
        {
            PackResult? result = solver == 0
                ? CategorySlotModel.Solve(new[] { request }, baseline, 5, out _).Grids[0]
                : CategoryPackingModel.Solve(new[] { request }, baseline, 5, out _)?.Grids[0];
            Assert.NotNull(result);
            after = result!;
        }

        var templates = items.ToDictionary(item => item.Id, item => item.TemplateId);
        Assert.All(after.Placements.GroupBy(p => p.Y), row =>
            Assert.Single(row.Select(p => templates[p.Id]).Distinct()));
        Assert.All(after.Placements.GroupBy(p => templates[p.Id]), group =>
            Assert.Equal(1, group.Max(p => p.Y) - group.Min(p => p.Y)));
        Assert.True(CategoryPacking.Compare(request, after, before) < 0);
        AssertPreserved(request, before, after);
    }

    private static PackItem Item(string id, string category, int width, int height) =>
        new(id, category, width, height)
        {
            Required = true,
            SortType = category,
        };

    private static void AssertPreserved(PackRequest request, PackResult before, PackResult after)
    {
        CpSatPackerTests.AssertValid(request, before);
        CpSatPackerTests.AssertValid(request, after);
        Assert.True(after.Complete);
        Assert.Equal(before.Placements.Select(p => p.Id).OrderBy(id => id),
            after.Placements.Select(p => p.Id).OrderBy(id => id));
        Assert.Equal(OccupiedSlots(request, before), OccupiedSlots(request, after));
    }

    private static (int X, int Y, int Width, int Height)[] OccupiedSlots(
        PackRequest request, PackResult result) => result.Placements.Select(p =>
        {
            PackItem item = request.Items.Single(i => i.Id == p.Id);
            return (p.X, p.Y, Width: p.Rotated ? item.Height : item.Width,
                Height: p.Rotated ? item.Width : item.Height);
        }).OrderBy(p => p.Y).ThenBy(p => p.X).ToArray();
}
