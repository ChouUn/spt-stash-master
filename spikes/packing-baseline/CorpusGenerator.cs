using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ChouUn.StashMaster.Core.Packing;
using Newtonsoft.Json;

internal static class CorpusGenerator
{
    private const string Version = "splitmix32-freecell-v2";
    private static readonly string[] Families = { "stash", "container", "combined" };
    private static readonly string[] DensityBands = { "sparse", "dense", "full" };
    private static readonly string[] TemplateProfiles = { "repeated", "mixed", "diverse" };

    public static void Generate(int seed, int samples, string output)
    {
        if (samples < 1) throw new ArgumentOutOfRangeException(nameof(samples));
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("Output path is required.", nameof(output));
        if (File.Exists(output)) throw new IOException("Refusing to overwrite corpus: " + output);
        Catalog catalog = LoadCatalog(out string catalogHash);
        var cases = new List<CorpusCase>(checked(samples * Families.Length));
        // Sample-major order makes increasing --samples preserve the entire case prefix.
        for (int sample = 0; sample < samples; sample++)
        {
            for (int family = 0; family < Families.Length; family++)
                cases.Add(CreateCase(catalog, seed, sample, family));
        }
        Validate(catalog, cases);
        var corpus = new CorpusFile
        {
            Seed = seed,
            CatalogSha256 = catalogHash,
            SourceHashes = new SortedDictionary<string, string>(catalog.SourceHashes, StringComparer.Ordinal),
            Cases = cases.ToArray(),
        };
        // Invariant numbers, explicit LF and BOM-free UTF-8 make bytes independent of host culture/OS.
        var text = new StringBuilder();
        using (var writer = new StringWriter(text, CultureInfo.InvariantCulture) { NewLine = "\n" })
        using (var json = new JsonTextWriter(writer) { Formatting = Formatting.Indented })
        {
            JsonSerializer.Create(new JsonSerializerSettings { Culture = CultureInfo.InvariantCulture })
                .Serialize(json, corpus);
            json.Flush();
        }
        byte[] bytes = new UTF8Encoding(false).GetBytes(text.ToString() + "\n");
        using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            file.Write(bytes, 0, bytes.Length);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "generated {0} cases ({1} per family), seed={2}, output={3}", cases.Count, samples, seed, output));
    }

    private static Catalog LoadCatalog(out string catalogHash)
    {
        using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("PackingCatalog")
            ?? throw new InvalidOperationException("Embedded resource PackingCatalog is missing.");
        using (var sha256 = SHA256.Create())
            catalogHash = BitConverter.ToString(sha256.ComputeHash(resource)).Replace("-", "").ToLowerInvariant();
        resource.Position = 0;
        using var reader = new StreamReader(resource, Encoding.UTF8);
        Catalog catalog = JsonConvert.DeserializeObject<Catalog>(reader.ReadToEnd())
            ?? throw new InvalidDataException("Packing catalog is empty.");
        if (catalog.Stash.Width != 10 || catalog.Stash.Height != 72)
            throw new InvalidDataException("Expected the real 10x72 stash dimensions.");
        string[] sizes = catalog.Containers.Select(s => s.Width + "x" + s.Height).ToArray();
        if (!sizes.SequenceEqual(new[] { "5x10", "7x7", "10x10", "14x14" }))
            throw new InvalidDataException("Expected the four balanced real container sizes in catalog order.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Template template in catalog.Templates.Concat(catalog.ContainerTemplates))
        {
            if (string.IsNullOrWhiteSpace(template.TemplateId) || !ids.Add(template.TemplateId)
                || template.Width < 1 || template.Height < 1 || template.SortType == null
                || template.SubcategoryPath == null || template.SubcategoryPath.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Invalid or duplicate catalog template: " + template.TemplateId);
        }
        if (!catalog.Templates.Any(t => t.Width == 1 && t.Height == 1)
            || !catalog.Templates.Any(t => t.Width * t.Height == 2)
            || !catalog.Templates.Any(t => t.Width * t.Height >= 6))
            throw new InvalidDataException("Catalog must contain singleton, domino and large templates.");
        foreach (GridSize size in catalog.Containers)
        {
            if (!catalog.ContainerTemplates.Any(t => t.GridWidth == size.Width && t.GridHeight == size.Height))
                throw new InvalidDataException("Container size has no concrete container template.");
        }
        return catalog;
    }

    private static CorpusCase CreateCase(Catalog catalog, int seed, int sample, int family)
    {
        uint sampleSeed = DeriveSeed(seed, sample, family);
        var random = new StableRandom(sampleSeed);
        string id = Families[family] + "-" + sample.ToString("D4", CultureInfo.InvariantCulture);
        int density = sample % DensityBands.Length;
        // Cross density with size/order/multiplicity rather than tying all strata together.
        int profile = (sample / 3 + family) % TemplateProfiles.Length;
        bool ordered = (sample / 4 + sample + family) % 2 == 0;
        int obstacleMode = (sample / 3 + sample + family) % 3;
        var grids = new List<CorpusGrid>();
        if (family == 1)
        {
            GridSize size = catalog.Containers[sample % catalog.Containers.Length];
            grids.Add(CreateGrid(catalog, random, id + "/container", size, null, null,
                density, profile, ordered, obstacleMode, Array.Empty<ContainerTemplate>()));
        }
        else
        {
            var children = new List<ContainerTemplate>();
            if (family == 2)
            {
                int count = 4 + sample % 9;
                for (int child = 0; child < count; child++)
                {
                    GridSize size = catalog.Containers[(sample + child) % catalog.Containers.Length];
                    ContainerTemplate[] candidates = catalog.ContainerTemplates
                        .Where(t => t.GridWidth == size.Width && t.GridHeight == size.Height)
                        .OrderBy(t => t.TemplateId, StringComparer.Ordinal).ToArray();
                    children.Add(candidates[random.Next(candidates.Length)]);
                }
                random.Shuffle(children);
            }
            CorpusGrid stash = CreateGrid(catalog, random, id + "/stash", catalog.Stash, null, null,
                density, profile, ordered, obstacleMode, children);
            grids.Add(stash);
            for (int child = 0; child < children.Count; child++)
            {
                ContainerTemplate template = children[child];
                grids.Add(CreateGrid(catalog, random, id + "/child-" + Number(child),
                    new GridSize { Width = template.GridWidth, Height = template.GridHeight },
                    stash.Id, stash.FixedContainers[child].Id, (density + child) % 3,
                    (profile + child) % 3, child % 2 == 0 ? ordered : !ordered,
                    (sample / 3 + sample + child) % 3, Array.Empty<ContainerTemplate>()));
            }
        }
        return new CorpusCase
        {
            Id = id,
            Family = Families[family],
            SampleSeed = sampleSeed,
            DensityBand = DensityBands[density],
            TemplateProfile = TemplateProfiles[profile],
            Grids = grids.ToArray(),
        };
    }

    private static CorpusGrid CreateGrid(Catalog catalog, StableRandom random, string id, GridSize size,
        string? parentGridId, string? containerItemId, int density, int profile, bool ordered,
        int obstacleMode, IReadOnlyList<ContainerTemplate> containers)
    {
        var occupied = new bool[size.Width * size.Height];
        var fixedBlocks = new List<FixedBlock>();
        var fixedContainers = new List<FixedContainer>();
        var retained = new List<Tile>();
        int serial = 0;
        // 父容器在仓库中固定占位，子网格仍独立参与同一次最终排布。
        foreach (ContainerTemplate template in containers)
        {
            Position position = FindPosition(occupied, size, template, random, false)
                ?? throw new InvalidOperationException("Container footprint does not fit its parent stash.");
            Tile container = Place(occupied, size, template, position, id, serial++);
            var block = new FixedBlock(position.X, position.Y,
                position.Rotated ? template.Height : template.Width,
                position.Rotated ? template.Width : template.Height);
            fixedBlocks.Add(block);
            fixedContainers.Add(new FixedContainer
            {
                Id = container.Id, TemplateId = template.TemplateId, Block = block,
            });
        }
        int fixedArea = fixedBlocks.Sum(b => b.Width * b.Height);
        int fixedTarget = fixedArea + (obstacleMode == 0 ? 0
            : size.Width * size.Height * (obstacleMode == 1 ? 2 : 6) / 100);
        while (fixedArea < fixedTarget)
        {
            int width = 1 + random.Next(Math.Min(4, size.Width));
            int height = 1 + random.Next(Math.Min(3, size.Height));
            if (width * height > fixedTarget - fixedArea) { width = 1; height = 1; }
            var footprint = new Template { Width = width, Height = height };
            Position position = FindPosition(occupied, size, footprint, random, true)
                ?? throw new InvalidOperationException("Could not place a fixed obstacle.");
            var block = new FixedBlock(position.X, position.Y,
                position.Rotated ? height : width, position.Rotated ? width : height);
            Mark(occupied, size, block.X, block.Y, block.Width, block.Height);
            fixedBlocks.Add(block);
            fixedArea += width * height;
        }
        Shape[] palette = CreatePalette(catalog.Templates, random, profile);
        int available = occupied.Length - fixedArea;
        int percent = density == 0 ? 30 + random.Next(15)
            : density == 1 ? 86 + random.Next(10) : 100;
        int targetArea = available * percent / 100;
        int largeTarget = Math.Max(6, available * (4 + random.Next(4)) / 100);
        int largeArea = 0;
        Shape[] largeShapes = palette.Where(s => s.Area >= 6).ToArray();
        random.Shuffle(largeShapes);
        foreach (Shape shape in largeShapes)
        {
            if (largeArea + shape.Area > targetArea) continue;
            Template template = shape.Templates[random.Next(shape.Templates.Length)];
            Position? position = FindPosition(occupied, size, template, random, true);
            if (position == null) continue;
            retained.Add(Place(occupied, size, template, position.Value, id, serial++));
            largeArea += shape.Area;
            if (largeArea >= largeTarget) break;
        }
        if (largeArea == 0) throw new InvalidOperationException("No large item fits the grid witness.");

        // Tile every remaining free cell, then retain a shuffled subset. Unlike rejection-sampling
        // items into an almost-full grid, this cannot manufacture impossible requests or force a
        // predominantly 1x1 dense tail. Catalog population does not influence size frequencies.
        var tiles = new List<Tile>();
        var candidates = new List<Candidate>();
        for (int cell = 0; cell < occupied.Length; cell++)
        {
            if (occupied[cell]) continue;
            int x = cell % size.Width;
            int y = cell / size.Width;
            candidates.Clear();
            int totalWeight = 0;
            foreach (Shape shape in palette)
            {
                bool normal = Fits(occupied, size, x, y, shape.Width, shape.Height);
                bool rotated = shape.Width != shape.Height && Fits(occupied, size, x, y, shape.Height, shape.Width);
                if (!normal && !rotated) continue;
                // Each area bucket has a fixed total weight, not one weight per catalog entry.
                int weight = shape.Weight;
                candidates.Add(new Candidate(shape, normal, rotated, weight));
                totalWeight += weight;
            }
            if (totalWeight == 0) throw new InvalidOperationException("The singleton tile is missing.");
            int draw = random.Next(totalWeight);
            Candidate chosen = candidates[0];
            foreach (Candidate candidate in candidates)
            {
                chosen = candidate;
                if (draw < candidate.Weight) break;
                draw -= candidate.Weight;
            }
            bool transpose = !chosen.Normal || (chosen.Rotated && random.Next(2) == 0);
            Template selected = chosen.Shape.Templates[random.Next(chosen.Shape.Templates.Length)];
            // Shapes use canonical dimensions; the template always keeps its real catalog dimensions.
            bool rotatedTemplate = transpose ^ (selected.Width != chosen.Shape.Width);
            tiles.Add(Place(occupied, size, selected, new Position(x, y, rotatedTemplate), id, serial++));
        }
        random.Shuffle(tiles);
        int itemArea = retained.Sum(t => t.Template.Width * t.Template.Height);
        if (itemArea > targetArea)
            throw new InvalidOperationException("Mandatory large witnesses exceed target density.");
        foreach (Tile tile in tiles)
        {
            int area = tile.Template.Width * tile.Template.Height;
            if (itemArea + area > targetArea) continue;
            retained.Add(tile);
            itemArea += area;
        }
        if (density == 2 && itemArea != available)
            throw new InvalidOperationException("Full grids must occupy every non-obstacle cell.");
        PackItem[] items = retained.Select(t => new PackItem(t.Id, t.Template.TemplateId,
            t.Template.Width, t.Template.Height)
        {
            Required = true,
            SortType = t.Template.SortType,
            SubcategoryPath = t.Template.SubcategoryPath,
        }).ToArray();
        Placement[] current = retained.Select(t => new Placement(t.Id, t.Position.X, t.Position.Y,
            t.Position.Rotated)).ToArray();
        string[] order = ordered ? WitnessOrder(retained) : Array.Empty<string>();
        var request = new PackRequest(size.Width, size.Height, fixedBlocks.ToArray(), items)
        {
            CategoryOrder = order,
            Current = current,
        };
        return new CorpusGrid
        {
            Id = id,
            ParentGridId = parentGridId,
            ContainerItemId = containerItemId,
            FixedContainers = fixedContainers.ToArray(),
            DensityBand = DensityBands[density],
            TemplateProfile = TemplateProfiles[profile],
            TargetDensity = percent / 100m,
            AchievedDensity = decimal.Round((decimal)itemArea / available, 6),
            ItemCount = items.Length,
            ItemArea = itemArea,
            FixedArea = fixedArea,
            SingletonCount = items.Count(t => t.Width * t.Height == 1),
            LargeItemCount = items.Count(t => t.Width * t.Height >= 6),
            RotatedCount = current.Count(p => p.Rotated),
            TemplateCount = items.Select(t => t.TemplateId).Distinct(StringComparer.Ordinal).Count(),
            MaxTemplateMultiplicity = items.GroupBy(t => t.TemplateId, StringComparer.Ordinal).Max(g => g.Count()),
            Request = request,
        };
    }

    private static Shape[] CreatePalette(Template[] templates, StableRandom random, int profile)
    {
        var shapes = new List<Shape>();
        foreach (var group in templates.GroupBy(t => new { Width = Math.Min(t.Width, t.Height), Height = Math.Max(t.Width, t.Height) })
            .OrderBy(g => g.Key.Width).ThenBy(g => g.Key.Height))
        {
            Template[] choices = group.OrderBy(t => t.TemplateId, StringComparer.Ordinal).ToArray();
            random.Shuffle(choices);
            int count = profile == 0 ? 1 : profile == 1 ? Math.Min(4, choices.Length) : Math.Min(16, choices.Length);
            shapes.Add(new Shape(group.Key.Width, group.Key.Height, choices.Take(count).ToArray()));
        }
        int[] weights = { 24, 65, 10, 1 };
        for (int bucket = 0; bucket < weights.Length; bucket++)
        {
            Shape[] members = shapes.Where(s => Bucket(s.Area) == bucket).ToArray();
            foreach (Shape shape in members) shape.Weight = Math.Max(1, weights[bucket] * 100 / members.Length);
        }
        return shapes.ToArray();
    }

    private static int Bucket(int area) => area == 1 ? 0 : area == 2 ? 1 : area < 6 ? 2 : 3;

    private static string[] WitnessOrder(List<Tile> tiles) => tiles
        .GroupBy(t => t.Template.SortType, StringComparer.Ordinal)
        .Select(g => new
        {
            Type = g.Key,
            Center = g.Min(t => t.Position.Y) + g.Max(t => t.Position.Y
                + (t.Position.Rotated ? t.Template.Width : t.Template.Height)),
        })
        .OrderBy(g => g.Center).ThenBy(g => g.Type, StringComparer.Ordinal).Select(g => g.Type).ToArray();

    private static Position? FindPosition(bool[] occupied, GridSize size, Template template, StableRandom random, bool scattered)
    {
        Position? selected = null;
        int count = 0;
        bool preferRotated = template.Width != template.Height && random.Next(2) == 0;
        for (int y = 0; y < size.Height; y++)
        {
            for (int x = 0; x < size.Width; x++)
            {
                for (int orientation = 0; orientation < (template.Width == template.Height ? 1 : 2); orientation++)
                {
                    bool rotated = orientation == 0 ? preferRotated : !preferRotated;
                    int width = rotated ? template.Height : template.Width;
                    int height = rotated ? template.Width : template.Height;
                    if (!Fits(occupied, size, x, y, width, height)) continue;
                    if (!scattered) return new Position(x, y, rotated);
                    // Reservoir sampling enumerates legal placements without allocating a positions list.
                    if (random.Next(++count) == 0) selected = new Position(x, y, rotated);
                }
            }
        }
        return selected;
    }

    private static Tile Place(bool[] occupied, GridSize size, Template template, Position position, string id, int serial)
    {
        int width = position.Rotated ? template.Height : template.Width;
        int height = position.Rotated ? template.Width : template.Height;
        Mark(occupied, size, position.X, position.Y, width, height);
        return new Tile(id + "/item-" + Number(serial), template, position);
    }

    private static bool Fits(bool[] occupied, GridSize size, int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width < 1 || height < 1 || x > size.Width - width || y > size.Height - height) return false;
        for (int row = y; row < y + height; row++)
            for (int column = x; column < x + width; column++)
                if (occupied[row * size.Width + column]) return false;
        return true;
    }

    private static void Mark(bool[] occupied, GridSize size, int x, int y, int width, int height)
    {
        if (!Fits(occupied, size, x, y, width, height))
            throw new InvalidOperationException("Witness contains an overlap or out-of-bounds rectangle.");
        for (int row = y; row < y + height; row++)
            for (int column = x; column < x + width; column++)
                occupied[row * size.Width + column] = true;
    }

    private static void Validate(Catalog catalog, List<CorpusCase> cases)
    {
        var templates = catalog.Templates.Concat(catalog.ContainerTemplates).ToDictionary(t => t.TemplateId, StringComparer.Ordinal);
        var containerTemplates = catalog.ContainerTemplates.ToDictionary(t => t.TemplateId, StringComparer.Ordinal);
        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        var allItemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (CorpusCase sample in cases)
        {
            if (!caseIds.Add(sample.Id)) throw new InvalidOperationException("Duplicate case identity.");
            var grids = sample.Grids.ToDictionary(g => g.Id, StringComparer.Ordinal);
            int roots = 0;
            foreach (CorpusGrid grid in sample.Grids)
            {
                foreach (FixedContainer container in grid.FixedContainers)
                {
                    if (!allItemIds.Add(container.Id)
                        || !containerTemplates.TryGetValue(container.TemplateId, out ContainerTemplate template)
                        || grid.Request.Fixed.Count(b => b == container.Block) != 1
                        || !((container.Block.Width == template.Width && container.Block.Height == template.Height)
                            || (container.Block.Width == template.Height && container.Block.Height == template.Width)))
                        throw new InvalidOperationException("Fixed container identity or footprint was violated.");
                }
            }
            var containerIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (CorpusGrid grid in sample.Grids)
            {
                PackRequest request = grid.Request;
                var size = new GridSize { Width = request.Width, Height = request.Height };
                var occupied = new bool[request.Width * request.Height];
                foreach (FixedBlock block in request.Fixed)
                    Mark(occupied, size, block.X, block.Y, block.Width, block.Height);
                var items = request.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
                var placed = new HashSet<string>(StringComparer.Ordinal);
                foreach (PackItem item in request.Items)
                {
                    if (!allItemIds.Add(item.Id) || !item.Required || !templates.TryGetValue(item.TemplateId, out Template template)
                        || item.Width != template.Width || item.Height != template.Height || item.SortType != template.SortType
                        || !item.SubcategoryPath.SequenceEqual(template.SubcategoryPath))
                        throw new InvalidOperationException("Item conservation or catalog identity was violated.");
                }
                foreach (Placement placement in request.Current)
                {
                    if (!items.TryGetValue(placement.Id, out PackItem item) || !placed.Add(placement.Id))
                        throw new InvalidOperationException("Current contains an unknown or duplicate item.");
                    Mark(occupied, size, placement.X, placement.Y,
                        placement.Rotated ? item.Height : item.Width, placement.Rotated ? item.Width : item.Height);
                }
                if (placed.Count != items.Count || items.Count != grid.ItemCount
                    || request.Items.Sum(i => i.Width * i.Height) != grid.ItemArea
                    || request.Fixed.Sum(b => b.Width * b.Height) != grid.FixedArea)
                    throw new InvalidOperationException("Current is not a complete conserved witness.");
                if (grid.DensityBand == "full" && grid.ItemArea + grid.FixedArea != request.Width * request.Height)
                    throw new InvalidOperationException("Full grid contains unused cells.");
                if (CategoryOrder.Penalty(request, new PackResult(request.Current, Array.Empty<PackItem>())) != 0)
                    throw new InvalidOperationException("Current violates enabled category order.");
                if (grid.ParentGridId == null)
                {
                    roots++;
                    if (grid.ContainerItemId != null) throw new InvalidOperationException("Root has a container link.");
                }
                else
                {
                    if (!grids.TryGetValue(grid.ParentGridId, out CorpusGrid parent) || parent.ParentGridId != null
                        || grid.ContainerItemId == null || !containerIds.Add(grid.ContainerItemId))
                        throw new InvalidOperationException("Invalid, cyclic or duplicate parent-container link.");
                    FixedContainer? container = parent.FixedContainers.SingleOrDefault(i => i.Id == grid.ContainerItemId);
                    if (container == null || !containerTemplates.TryGetValue(container.TemplateId, out ContainerTemplate template)
                        || template.GridWidth != request.Width || template.GridHeight != request.Height)
                        throw new InvalidOperationException("Child grid does not match its concrete parent container.");
                }
            }
            if (containerIds.Count != sample.Grids.Sum(g => g.FixedContainers.Length))
                throw new InvalidOperationException("Fixed container has no child grid.");
            if (roots != 1 || (sample.Family == "combined" ? sample.Grids.Length < 5 || sample.Grids.Length > 13 : sample.Grids.Length != 1))
                throw new InvalidOperationException("Family topology invariant was violated.");
        }
    }

    private static string Number(int value) => value.ToString("D4", CultureInfo.InvariantCulture);

    private static uint DeriveSeed(int seed, int sample, int family)
    {
        // Unchecked uint arithmetic is part of the reproducible algorithm, including negative seeds.
        return Mix(unchecked((uint)seed + 0x9e3779b9u * ((uint)sample + 1u)
            + 0x85ebca6bu * ((uint)family + 1u)));
    }

    private static uint Mix(uint value)
    {
        unchecked
        {
            value = (value ^ (value >> 16)) * 0x21f0aaadu;
            value = (value ^ (value >> 15)) * 0x735a2d97u;
            return value ^ (value >> 15);
        }
    }

    private sealed class StableRandom
    {
        private uint _state;
        public StableRandom(uint seed) { _state = seed; }
        // SplitMix32: Weyl increment followed by the fixed avalanche above; never System.Random.
        private uint NextUInt() => Mix(_state = unchecked(_state + 0x9e3779b9u));
        public int Next(int exclusiveMax)
        {
            if (exclusiveMax <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
            uint bound = (uint)exclusiveMax;
            uint threshold = unchecked(0u - bound) % bound;
            uint value;
            do { value = NextUInt(); } while (value < threshold);
            return (int)(value % bound);
        }
        public void Shuffle<T>(IList<T> values)
        {
            for (int index = values.Count - 1; index > 0; index--)
            {
                int other = Next(index + 1);
                T temporary = values[index];
                values[index] = values[other];
                values[other] = temporary;
            }
        }
    }

    private readonly struct Position
    {
        public Position(int x, int y, bool rotated) { X = x; Y = y; Rotated = rotated; }
        public int X { get; }
        public int Y { get; }
        public bool Rotated { get; }
    }
    private sealed class Tile
    {
        public Tile(string id, Template template, Position position) { Id = id; Template = template; Position = position; }
        public string Id { get; }
        public Template Template { get; }
        public Position Position { get; }
    }
    private sealed class Shape
    {
        public Shape(int width, int height, Template[] templates) { Width = width; Height = height; Templates = templates; }
        public int Width { get; }
        public int Height { get; }
        public int Area => Width * Height;
        public Template[] Templates { get; }
        public int Weight { get; set; }
    }
    private readonly struct Candidate
    {
        public Candidate(Shape shape, bool normal, bool rotated, int weight) { Shape = shape; Normal = normal; Rotated = rotated; Weight = weight; }
        public Shape Shape { get; }
        public bool Normal { get; }
        public bool Rotated { get; }
        public int Weight { get; }
    }
    private class Template
    {
        public string TemplateId { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public string SortType { get; set; } = "";
        public string[] SubcategoryPath { get; set; } = Array.Empty<string>();
    }
    private sealed class ContainerTemplate : Template
    {
        public int GridWidth { get; set; }
        public int GridHeight { get; set; }
    }
    private sealed class GridSize
    {
        public int Width { get; set; }
        public int Height { get; set; }
    }
    private sealed class Catalog
    {
        public Dictionary<string, string> SourceHashes { get; set; } = new Dictionary<string, string>();
        public GridSize Stash { get; set; } = new GridSize();
        public GridSize[] Containers { get; set; } = Array.Empty<GridSize>();
        public Template[] Templates { get; set; } = Array.Empty<Template>();
        public ContainerTemplate[] ContainerTemplates { get; set; } = Array.Empty<ContainerTemplate>();
    }
    private sealed class CorpusFile
    {
        public int Schema { get; set; } = 3;
        public string GeneratorVersion { get; set; } = Version;
        public int Seed { get; set; }
        public string CatalogSha256 { get; set; } = "";
        public string WitnessMethod { get; set; } = "Legal free-cell tiling with shuffled density subsampling; no solver";
        public string CategoryOrderMethod { get; set; } = "On/off stratified; enabled order follows Current category extent centers";
        public string Scope { get; set; } = "Synthetic final-packing corpus using observed dimensions/classification; no game filters, folding, stacking or collection";
        public SortedDictionary<string, string> SourceHashes { get; set; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public CorpusCase[] Cases { get; set; } = Array.Empty<CorpusCase>();
    }
    private sealed class FixedContainer
    {
        public string Id { get; set; } = "";
        public string TemplateId { get; set; } = "";
        public FixedBlock Block { get; set; } = null!;
    }
    private sealed class CorpusCase
    {
        public string Id { get; set; } = "";
        public string Family { get; set; } = "";
        public uint SampleSeed { get; set; }
        public string DensityBand { get; set; } = "";
        public string TemplateProfile { get; set; } = "";
        public CorpusGrid[] Grids { get; set; } = Array.Empty<CorpusGrid>();
    }
    private sealed class CorpusGrid
    {
        public string Id { get; set; } = "";
        public string? ParentGridId { get; set; }
        public string? ContainerItemId { get; set; }
        public FixedContainer[] FixedContainers { get; set; } = Array.Empty<FixedContainer>();
        public string DensityBand { get; set; } = "";
        public string TemplateProfile { get; set; } = "";
        public decimal TargetDensity { get; set; }
        public decimal AchievedDensity { get; set; }
        public int ItemCount { get; set; }
        public int ItemArea { get; set; }
        public int FixedArea { get; set; }
        public int SingletonCount { get; set; }
        public int LargeItemCount { get; set; }
        public int RotatedCount { get; set; }
        public int TemplateCount { get; set; }
        public int MaxTemplateMultiplicity { get; set; }
        public PackRequest Request { get; set; } = null!;
    }
}
