using System;
using System.IO;
using System.Linq;
using ChouUn.StashMaster.Core.Packing;
using Xunit;
using Xunit.Abstractions;

namespace ChouUn.StashMaster.Core.Tests.Packing;

public sealed class ActualStashTests
{
    private readonly ITestOutputHelper _output;

    public ActualStashTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void 垃圾箱几何与合成层级保持完整且质量不退步()
    {
        PackRequest request = Read("hierarchy-junk.csv", 14, 14);
        PackResult baseline = new CpSatPacker().Pack(request, 0);

        PackResult result = new CpSatPacker().Pack(request, 1);

        _output.WriteLine(result.Diagnostic);
        Assert.True(result.Complete);
        Assert.Equal(134, CpSatPackerTests.Area(request, result));
        Assert.Equal(10, Height(request, result));
        Assert.True(CategoryPacking.Score(request, result)
            <= CategoryPacking.Score(request, baseline));
        CpSatPackerTests.AssertValid(request, result);
    }

    [Fact]
    public void 整理后仓库几何继续改善合成父类聚合且空间不退步()
    {
        PackRequest request = Read("hierarchy-stash.csv");
        var before = new PackResult(request.Current, Array.Empty<PackItem>());
        var packer = new CachedPacker(new CpSatPacker());

        PackResult result = packer.Pack(request, 1);

        _output.WriteLine(result.Diagnostic);
        _output.WriteLine("before=" + string.Join(",", CategoryPacking.Spans(
            request, before)) + "; after=" + string.Join(",",
                CategoryPacking.Spans(request, result)));
        Assert.True(result.Complete);
        Assert.Equal(606, CpSatPackerTests.Area(request, result));
        Assert.Equal(61, Height(request, result));
        Assert.True(CategoryPacking.Score(request, result)
            < CategoryPacking.Score(request, before));
        CpSatPackerTests.AssertValid(request, result);
        PackResult again = packer.Pack(request with { Current = result.Placements });
        Assert.Equal(result.Placements, again.Placements);
    }

    [Fact]
    public void 仓库几何在一秒预算内实质改善合成类别聚合()
    {
        PackRequest request = Read();
        var before = new PackResult(request.Current, Array.Empty<PackItem>());
        Assert.Equal(322, request.Items.Count);
        Assert.Equal(606, CpSatPackerTests.Area(request, before));
        Assert.Equal(61, Height(request, before));
        CpSatPackerTests.AssertValid(request, before);

        var packer = new CachedPacker(new CpSatPacker());
        PackResult result = packer.Pack(request, 1);

        _output.WriteLine(result.Diagnostic);
        CpSatPackerTests.AssertValid(request, result);
        Assert.True(result.Complete);
        Assert.Equal(61, Height(request, result));
        Assert.True(CategoryPacking.Score(request, result)
            < CategoryPacking.Score(request, before));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        PackResult again = packer.Pack(request with { Current = result.Placements }, 1);
        Assert.Equal(result.Placements, again.Placements);
        Assert.True(elapsed.ElapsedMilliseconds < 100);
        _output.WriteLine($"unchanged repeat: {elapsed.ElapsedMilliseconds} ms");
    }

    private static int Height(PackRequest request, PackResult result) =>
        result.Placements.Max(p => p.Y + (p.Rotated
            ? request.Items.Single(i => i.Id == p.Id).Width
            : request.Items.Single(i => i.Id == p.Id).Height));

    // 存档位置 + 运行时模组模板 + 游戏/Foldables 尺寸公式，身份已匿名化。
    // 分类路径只是合成层级，不代表真实游戏 taxonomy：首段定义 FixtureGroup，
    // 其余段才是类型内相对子链。CategoryOrderTests 覆盖类型时必须丢弃合成子链。
    internal static PackRequest Read(string fixture = "current-stash.csv",
        int width = 10, int height = 72)
    {
        using Stream stream = typeof(ActualStashTests).Assembly
            .GetManifestResourceStream(typeof(ActualStashTests).Namespace
                + ".Fixtures." + fixture)!;
        using var reader = new StreamReader(stream);
        string[][] data = reader.ReadToEnd().Split(new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(',')).ToArray();
        var paths = data.ToDictionary(row => row[0], row => row[2] == "-1"
            ? Array.Empty<string>() : row[2].Split('/'));
        int[][] rows = data.Select(row => row.Select((value, index) =>
            index == 2 ? 0 : int.Parse(value)).ToArray()).ToArray();
        return new PackRequest(width, height, rows.Where(r => r[8] != 0)
            .Select(r => new FixedBlock(r[5], r[6],
                r[7] != 0 ? r[4] : r[3], r[7] != 0 ? r[3] : r[4])).ToArray(),
            rows.Where(r => r[8] == 0).Select(r =>
                new PackItem(r[0].ToString(), r[1].ToString("D3"), r[3], r[4])
                {
                    Required = true,
                    SortType = paths[r[0].ToString()].Length == 0
                        ? "" : "FixtureGroup:" + paths[r[0].ToString()][0],
                    SubcategoryPath = paths[r[0].ToString()].Skip(1).ToArray(),
                }).ToArray())
        {
            Current = rows.Where(r => r[8] == 0).Select(r =>
                new Placement(r[0].ToString(), r[5], r[6], r[7] != 0)).ToArray(),
        };
    }
}
