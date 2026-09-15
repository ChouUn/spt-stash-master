using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using ChouUn.StashMaster.Core.Packing;
using Xunit;
using Xunit.Abstractions;

namespace ChouUn.StashMaster.Core.Tests.Packing;

public sealed class CategoryPackingTests
{
    private readonly ITestOutputHelper _output;

    public CategoryPackingTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void 单类别单格装满时不因剩余候选继续求解()
    {
        var request = new PackRequest(7, 7, Array.Empty<FixedBlock>(),
            Enumerable.Range(0, 59).Select(i => Item(i.ToString(), "ammo")
                with
            { Required = i < 40 }).ToArray())
        {
            Current = Enumerable.Range(0, 40).Select(i =>
                new Placement(i.ToString(), i % 7, i / 7, false)).ToArray(),
        };

        PackResult result = new CpSatPacker().Pack(request);

        Assert.Equal(49, result.Placements.Count);
        Assert.Equal(6, CategoryPacking.Span(request, result));
        Assert.DoesNotContain(result.Unplaced, i => i.Required);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void 混合尺寸求解与穷举的空间层级及左上目标一致(
        bool hierarchy, bool equivalent)
    {
        PackItem[] items =
        {
            Item("a", "drink", 1, 2), Item("b", "drink"),
            Item("c", "bag", 2, 2), Item("d", "bag"),
        };
        if (hierarchy)
        {
            items[0] = items[0] with { SortType = "weapon", SubcategoryPath = new[] { "rifle" } };
            items[1] = items[1] with { SortType = "gear", SubcategoryPath = new[] { "bag" } };
            items[2] = items[2] with { SortType = "weapon", SubcategoryPath = new[] { "shotgun" } };
            items[3] = items[3] with { SortType = "gear", SubcategoryPath = new[] { "bag" } };
        }
        if (equivalent)
        {
            items[1] = items[0] with { Id = "b" };
        }
        var request = new PackRequest(3, 3, Array.Empty<FixedBlock>(), items);
        var occupied = new bool[3, 3];
        var placed = new List<Placement>();
        var optimum = (Height: int.MaxValue, Parent: int.MaxValue, Child: int.MaxValue,
            Rows: int.MaxValue, Columns: int.MaxValue);
        Search(0);

        // 此用例验证最优目标与穷举一致，不把默认一秒预算当作最优证明。
        PackResult result = new CpSatPacker().Pack(request, 5);
        _output.WriteLine(result.Diagnostic);

        Assert.Equal(optimum, (CpSatPackerTests.Height(request, result),
            CategoryPacking.Span(request, result),
            CategoryPacking.Span(request, result, 1),
            (int)CategoryPacking.CoordinateSum(request, result, false),
            (int)CategoryPacking.CoordinateSum(request, result, true)));
        CpSatPackerTests.AssertValid(request, result);

        void Search(int index)
        {
            if (index == items.Length)
            {
                var candidate = new PackResult(placed, Array.Empty<PackItem>());
                var score = (Height: CpSatPackerTests.Height(request, candidate),
                    Parent: CategoryPacking.Span(request, candidate),
                    Child: CategoryPacking.Span(request, candidate, 1),
                    Rows: Enumerable.Range(0, 3).Sum(y => Enumerable.Range(0, 3)
                        .Where(x => occupied[x, y]).Sum(_ => y)),
                    Columns: Enumerable.Range(0, 3).Sum(x => Enumerable.Range(0, 3)
                        .Where(y => occupied[x, y]).Sum(_ => x)));
                if (score.CompareTo(optimum) < 0)
                {
                    optimum = score;
                }
                return;
            }
            PackItem item = items[index];
            foreach (bool rotated in new[] { false, true })
            {
                int width = rotated ? item.Height : item.Width;
                int height = rotated ? item.Width : item.Height;
                for (int y = 0; y <= 3 - height; y++)
                {
                    for (int x = 0; x <= 3 - width; x++)
                    {
                        var cells = Enumerable.Range(x, width).SelectMany(cx =>
                            Enumerable.Range(y, height).Select(cy => (cx, cy)))
                            .ToArray();
                        if (cells.Any(c => occupied[c.cx, c.cy]))
                        {
                            continue;
                        }
                        foreach (var c in cells) { occupied[c.cx, c.cy] = true; }
                        placed.Add(new Placement(item.Id, x, y, rotated));
                        Search(index + 1);
                        placed.RemoveAt(placed.Count - 1);
                        foreach (var c in cells) { occupied[c.cx, c.cy] = false; }
                    }
                }
            }
        }
    }

    [Fact]
    public void 底部固定障碍不阻止上方类别聚合()
    {
        PackRequest request = Alternating() with
        {
            Height = 8,
            Fixed = new[] { new FixedBlock(0, 7, 1, 1) },
        };

        PackResult result = new CpSatPacker().Pack(request);

        Assert.Equal(2, CategoryPacking.Span(request, result));
        Assert.Equal(3, result.Placements.Max(p => p.Y));
        CpSatPackerTests.AssertValid(request, result);
    }

    [Fact]
    public void 大仓库聚合限时返回_不降低空间利用率()
    {
        var request = new PackRequest(10, 72,
            new[] { new FixedBlock(0, 70, 10, 2) },
            Enumerable.Range(0, 320).Select(i => Item(i.ToString("D3"),
                "category" + i % 8, i < 200 ? 1 : 2, i < 200 ? 1 : 2)).ToArray());
        PackResult baseline = new CpSatPacker().Pack(request, 0);
        var elapsed = Stopwatch.StartNew();

        PackResult result = new CpSatPacker().Pack(request, 1);

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3));
        Assert.True(result.Complete);
        Assert.True(CpSatPackerTests.Height(request, result)
            <= CpSatPackerTests.Height(request, baseline));
        Assert.True(CategoryPacking.Span(request, result)
            <= CategoryPacking.Span(request, baseline));
        CpSatPackerTests.AssertValid(request, result);
        _output.WriteLine(result.Diagnostic);
    }

    [Fact]
    public void 空间已最优时仍全局聚合同类_纯单格也参与()
    {
        var request = Alternating();
        PackResult baseline = new CpSatPacker().Pack(request, 0);
        Assert.Equal(4, CategoryPacking.Span(request, baseline));

        PackResult result = new CpSatPacker().Pack(request);

        Assert.Equal(2, CategoryPacking.Span(request, result));
        Assert.Equal(4, CpSatPackerTests.Height(request, result));
        CpSatPackerTests.AssertValid(request, result);
        PackResult again = new CpSatPacker().Pack(request with
        {
            Current = result.Placements,
        });
        Assert.Equal(result.Placements, again.Placements);
    }

    [Fact]
    public void 跨度统计实际覆盖行_包括转置高度_固定障碍不计入()
    {
        var request = new PackRequest(5, 20,
            new[] { new FixedBlock(0, 19, 5, 1) }, new[]
            {
                Item("a", "drink", 3, 1), Item("b", "drink"),
                Item("unknown", null),
            });
        var result = new PackResult(new[]
        {
            new Placement("a", 0, 1, true), new Placement("b", 1, 0, false),
            new Placement("unknown", 0, 10, false),
        }, Array.Empty<PackItem>());

        Assert.Equal(3, CategoryPacking.Span(request, result));
        CpSatPackerTests.AssertValid(request, result);
    }

    [Fact]
    public void 空间收益优先于聚合_不能少装物品换取更小跨度()
    {
        var request = new PackRequest(2, 3, Array.Empty<FixedBlock>(), new[]
        {
            Item("a", "drink", 1, 2) with { Required = false },
            Item("b", "drink", 1, 2) with { Required = false },
            Item("c", "gun", 2, 1) with { Required = false },
        });

        PackResult result = new CpSatPacker().Pack(request);

        Assert.True(result.Complete);
        Assert.Equal(6, CpSatPackerTests.Area(request, result));
        Assert.Equal(3, CpSatPackerTests.Height(request, result));
        CpSatPackerTests.AssertValid(request, result);
    }

    [Fact]
    public void 收纳不为聚合改动同面积布局_可选类别缺席时跨度为零()
    {
        PackRequest first = Alternating();
        PackRequest second = new(1, 1, Array.Empty<FixedBlock>(), new[]
        {
            Item("too-large", "other", 5, 5) with { Required = false },
        });
        var requests = new[] { first, second };

        ContainerPackResult result = new ContainerPacker(new CpSatPacker())
            .Pack(requests, 1);

        Assert.Equal(4, CategoryPacking.Span(first, result.Grids[0]));
        Assert.Equal(0, CategoryPacking.Span(second, result.Grids[1]));
        Assert.Empty(result.Grids[1].Placements);
        for (int i = 0; i < requests.Length; i++)
        {
            CpSatPackerTests.AssertValid(requests[i], result.Grids[i]);
        }
    }

    private static PackRequest Alternating() => new(1, 4,
        Array.Empty<FixedBlock>(), new[]
        {
            Item("a", "drink"), Item("b", "bag"),
            Item("c", "drink"), Item("d", "bag"),
        });

    private static PackItem Item(string id, string? category, int w = 1, int h = 1) =>
        new(id, id, w, h)
        {
            SortType = category ?? "",
            Required = true
        };
}
