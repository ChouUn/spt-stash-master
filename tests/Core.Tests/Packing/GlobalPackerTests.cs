using System;
using System.Diagnostics;
using System.Linq;
using ChouUn.StashMaster.Core.Packing;
using Xunit;
using Xunit.Abstractions;

namespace ChouUn.StashMaster.Core.Tests.Packing;

public sealed class GlobalPackerTests
{
    private readonly ITestOutputHelper _output;
    public GlobalPackerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void 各网格先完整加权再相加_不按全局父层拒绝更低总分()
    {
        PackRequest[] requests = Enumerable.Range(0, 2).Select(grid =>
            new PackRequest(1, 4, Array.Empty<FixedBlock>(),
                Enumerable.Range(0, 4).Select(i => new PackItem(
                    $"{grid}-{i}", "same", 1, 1)
                {
                    Required = true,
                    SortType = "c" + i / 2,
                    SubcategoryPath = grid == 0 ? Array.Empty<string>()
                        : new[] { "branch-" + i },
                }).ToArray())).ToArray();
        PackResult Layout(int grid, bool alternating) => new(
            Enumerable.Range(0, 4).Select(i => new Placement($"{grid}-{i}", 0,
                alternating ? i % 2 * 2 + i / 2 : i, false)).ToArray(),
            Array.Empty<PackItem>());
        var before = new ContainerPackResult(new[]
            { Layout(0, false), Layout(1, true) });
        var next = new ContainerPackResult(new[] { Layout(0, true), Layout(1, false) });
        for (int grid = 0; grid < requests.Length; grid++)
        {
            CpSatPackerTests.AssertValid(requests[grid], before.Grids[grid]);
            CpSatPackerTests.AssertValid(requests[grid], next.Grids[grid]);
        }

        // 网格 1 的每个顶层都有两个有效子分支，因而其顶层收益权重更高。
        Assert.True(CategoryPacking.Score(requests[0], next.Grids[0])
            > CategoryPacking.Score(requests[0], before.Grids[0]));
        Assert.True(CategoryPacking.Score(requests[1], next.Grids[1])
            < CategoryPacking.Score(requests[1], before.Grids[1]));
        Assert.True(CategoryPacking.Compare(requests, next, before) < 0);
    }

    [Fact]
    public void 仓库与多个容器联合排布且归属不变()
    {
        PackRequest[] requests =
        {
            ActualStashTests.Read("hierarchy-stash.csv"),
            Rename(ActualStashTests.Read("hierarchy-junk.csv", 14, 14), "junk"),
        };
        var packer = new CachedPacker(new CpSatPacker());
        ContainerPackResult baseline = new GlobalPacker(new CpSatPacker())
            .Pack(requests, 0);
        var clock = Stopwatch.StartNew();
        ContainerPackResult result = packer.PackAll(requests, 2.6);
        _output.WriteLine($"{clock.ElapsedMilliseconds}ms {result.Diagnostic}");
        for (int i = 0; i < requests.Length; i++)
        {
            Assert.True(result.Grids[i].Complete);
            CpSatPackerTests.AssertValid(requests[i], result.Grids[i]);
            _output.WriteLine(string.Join(",", CategoryPacking.Spans(
                requests[i], result.Grids[i])));
        }
        Assert.Equal(606, CpSatPackerTests.Area(requests[0], result.Grids[0]));
        Assert.Equal(61, CpSatPacker.Height(requests[0], result.Grids[0]));
        // 停滞退出可能早于后续改善，限时结果须保持完整分数不退步。
        Assert.True(CategoryPacking.Compare(requests, result, baseline) <= 0);
        ContainerPackResult again = packer.PackAll(requests.Select((r, i) => r with
        { Current = result.Grids[i].Placements }).ToArray(), 2.6);
        Assert.Equal(result.Grids, again.Grids);
    }

    [Fact]
    public void 二十个容器不按数量累加求解时限()
    {
        PackRequest junk = ActualStashTests.Read("hierarchy-junk.csv", 14, 14);
        PackResult baseline = new GlobalPacker(new CpSatPacker()).Pack(new[] { junk }, 0).Grids[0];
        PackRequest[] requests = Enumerable.Range(0, 20)
            .Select(i => Rename(junk, "box" + i)).ToArray();
        var clock = Stopwatch.StartNew();

        ContainerPackResult result = new GlobalPacker(new CpSatPacker())
            .Pack(requests, 1);

        _output.WriteLine($"{clock.ElapsedMilliseconds}ms {result.Diagnostic}");
        Assert.True(clock.Elapsed.TotalSeconds < 6);
        for (int i = 0; i < requests.Length; i++)
        {
            Assert.True(result.Grids[i].Complete);
            CpSatPackerTests.AssertValid(requests[i], result.Grids[i]);
            Assert.True(CategoryPacking.Compare(requests[i], result.Grids[i],
                baseline with
                {
                    Placements = baseline.Placements.Select(p => p with
                        { Id = "box" + i + ":" + p.Id }).ToArray(),
                }) <= 0);
        }
    }

    [Fact]
    public void 全局缓存随网格状态变化失效()
    {
        var request = new PackRequest(2, 3, Array.Empty<FixedBlock>(), new[]
        {
            new PackItem("a", "t", 1, 1) { Required = true },
        });
        var packer = new CachedPacker(new CpSatPacker());
        ContainerPackResult first = packer.PackAll(new[] { request }, 1);
        Assert.Equal(first.Grids, packer.PackAll(new[] { request }, 1).Grids);
        ContainerPackResult changed = packer.PackAll(new[] { request with
        {
            Fixed = new[] { new FixedBlock(0, 0, 1, 1) },
        } }, 1);
        Assert.Equal(1, Assert.Single(changed.Grids[0].Placements).X);
    }

    [Fact]
    public void 单一类型容器参与整次排布时也应压紧()
    {
        // 实际八行弹匣布局的匿名几何；保留模板分组和输入次序。
        (int Template, int X, int Y)[] layout =
        {
            (0, 0, 0), (0, 1, 0), (0, 2, 0), (4, 2, 4), (4, 3, 4),
            (4, 4, 4), (4, 1, 4), (2, 3, 2), (2, 2, 2), (2, 4, 2),
            (3, 0, 4), (5, 0, 6), (5, 1, 6), (1, 0, 2), (1, 3, 0),
            (1, 1, 2), (1, 4, 0),
        };
        var magazines = new PackRequest(5, 10, Array.Empty<FixedBlock>(),
            layout.Select((p, i) => new PackItem(
                "magazine-" + i, "magazine-" + p.Template, 1, 2)
            {
                Required = true,
                SortType = "Magazines",
            }).ToArray())
        {
            Current = layout.Select((p, i) =>
                new Placement("magazine-" + i, p.X, p.Y, false)).ToArray(),
        };
        PackRequest[] requests =
        {
            ActualStashTests.Read("hierarchy-stash.csv"), magazines,
            Rename(ActualStashTests.Read("hierarchy-junk.csv", 14, 14), "junk"),
        };

        ContainerPackResult result = new GlobalPacker(new CpSatPacker()).Pack(requests, 3);

        for (int grid = 0; grid < requests.Length; grid++)
        {
            Assert.True(result.Grids[grid].Complete);
            CpSatPackerTests.AssertValid(requests[grid], result.Grids[grid]);
        }
        Assert.Equal(34, CpSatPackerTests.Area(magazines, result.Grids[1]));
        Assert.Equal(7, CpSatPacker.Height(magazines, result.Grids[1]));
    }

    private static PackRequest Rename(PackRequest request, string prefix)
        => request with
        {
            Items = request.Items.Select(i => i with { Id = prefix + ":" + i.Id })
                .ToArray(),
            Current = request.Current.Select(p => p with { Id = prefix + ":" + p.Id })
                .ToArray(),
        };
}
