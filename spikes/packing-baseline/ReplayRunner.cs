using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using ChouUn.StashMaster.Core.Packing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class ReplayRunner
{
    internal static void Run(string corpusPath, double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds));

        // Validate the entire corpus before even the warmup calls production code.
        JObject corpus = JObject.Parse(File.ReadAllText(corpusPath), new JsonLoadSettings
        {
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
        });
        if (Integer(corpus, "Schema") != 3)
            throw new InvalidDataException("Unsupported corpus schema (expected 3).");
        Text(corpus, "GeneratorVersion");
        Integer(corpus, "Seed");
        JArray entries = Array(corpus, "Cases");
        if (entries.Count == 0) throw new InvalidDataException("Corpus has no cases.");
        var cases = new List<Case>();
        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (JToken entry in entries)
        {
            Case sample = ReadCase(Object(entry, "case"));
            if (!caseIds.Add(sample.Id))
                throw new InvalidDataException("Duplicate case ID: " + sample.Id);
            cases.Add(sample);
        }

        Case? warmup = cases.Where(c => c.Family == "container")
            .OrderBy(c => (long)c.Requests[0].Width * c.Requests[0].Height)
            .ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();
        if (warmup == null)
            throw new InvalidDataException("Corpus needs a container case for deterministic warmup.");
        new CachedPacker(new CpSatPacker()).PackAll(warmup.FreshRequests(), seconds);
        Console.WriteLine("READY");
        Console.Out.Flush();

        string? command;
        while ((command = Console.ReadLine()) != null)
        {
            if (!int.TryParse(command, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || index < 0 || index >= cases.Count)
                throw new InvalidDataException("Expected a valid zero-based case index; received: " + command);
            Case sample = cases[index];
            PackRequest[] requests = sample.FreshRequests();
            var packer = new CachedPacker(new CpSatPacker());
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var clock = new Stopwatch();
            clock.Start();
            ContainerPackResult result = packer.PackAll(requests, seconds);
            clock.Stop();
            Console.WriteLine(Report(sample, requests, result, clock.Elapsed.TotalMilliseconds)
                .ToString(Formatting.None));
            Console.Out.Flush();
        }
    }

    private static Case ReadCase(JObject entry)
    {
        string id = Text(entry, "Id");
        string family = Text(entry, "Family");
        if (family != "stash" && family != "container" && family != "combined")
            throw new InvalidDataException(id + ": unknown family " + family);
        JToken sampleSeed = Required(entry, "SampleSeed", JTokenType.Integer);
        long seed = sampleSeed.Value<long>();
        if (seed < uint.MinValue || seed > uint.MaxValue)
            throw new InvalidDataException(id + ": SampleSeed is not uint.");
        JArray grids = Array(entry, "Grids");
        if ((family == "combined" && grids.Count < 3)
            || (family != "combined" && grids.Count != 1))
            throw new InvalidDataException(id + ": expected one grid, or a stash and multiple container grids.");
        var gridIds = new HashSet<string>(StringComparer.Ordinal);
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<JObject>();
        var requests = new List<PackRequest>();
        var ids = new List<string>();
        var parents = new List<string?>();
        var containers = new List<string?>();
        var fixedContainers = new List<HashSet<string>>();
        foreach (JToken token in grids)
        {
            JObject grid = Object(token, id + " grid");
            string gridId = Text(grid, "Id");
            if (!gridIds.Add(gridId))
                throw new InvalidDataException(id + ": duplicate grid ID " + gridId);
            ids.Add(gridId);
            parents.Add(NullableText(grid, "ParentGridId"));
            containers.Add(NullableText(grid, "ContainerItemId"));
            JObject source = Object(grid["Request"], gridId + " Request");
            ValidateRequestShape(source);
            PackRequest request = source.ToObject<PackRequest>()!;
            var errors = new List<string>();
            if (request.Width <= 0 || request.Height <= 0)
                errors.Add("nonpositive grid dimensions");
            if (request.Items.Count == 0) errors.Add("empty item set");
            var localIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (PackItem item in request.Items)
            {
                if (!localIds.Add(item.Id)) errors.Add("duplicate item ID " + item.Id);
                if (!itemIds.Add(item.Id)) errors.Add("item ID shared across grids " + item.Id);
                if (!item.Required) errors.Add("item is not Required: " + item.Id);
                if (item.Width <= 0 || item.Height <= 0)
                    errors.Add("nonpositive item dimensions: " + item.Id);
            }
            var fixedIds = new HashSet<string>(StringComparer.Ordinal);
            var containerBlocks = new HashSet<FixedBlock>();
            foreach (JToken fixedToken in Array(grid, "FixedContainers"))
            {
                JObject container = Object(fixedToken, "fixed container");
                string containerId = Text(container, "Id");
                Text(container, "TemplateId");
                JObject block = Object(container["Block"], "fixed container block");
                var rectangle = new FixedBlock(Integer(block, "X"), Integer(block, "Y"),
                    Integer(block, "Width"), Integer(block, "Height"));
                containerBlocks.Add(rectangle);
                if (!fixedIds.Add(containerId) || !itemIds.Add(containerId))
                    errors.Add("duplicate or movable fixed container " + containerId);
                if (request.Fixed.Count(b => b == rectangle) != 1)
                    errors.Add("fixed container must match exactly one obstacle: " + containerId);
            }
            fixedContainers.Add(fixedIds);
            if (containerBlocks.Count != fixedIds.Count)
                errors.Add("fixed containers must occupy distinct obstacles");
            string densityBand = Text(grid, "DensityBand");
            if (densityBand != "sparse" && densityBand != "dense" && densityBand != "full")
                errors.Add("unknown density band " + densityBand);
            if (densityBand == "full" && request.Items.Sum(i => (long)i.Width * i.Height)
                + request.Fixed.Sum(b => (long)b.Width * b.Height) != (long)request.Width * request.Height)
                errors.Add("full grid contains unused cells");
            if (request.CategoryOrder.Distinct(StringComparer.Ordinal).Count() != request.CategoryOrder.Count)
                errors.Add("duplicate CategoryOrder entries");
            if (errors.Count == 0)
                ValidateLayout(request, new PackResult(request.Current, System.Array.Empty<PackItem>()), errors);
            if (errors.Count != 0)
                throw new InvalidDataException(id + "/" + gridId + ": " + string.Join("; ", errors));
            sources.Add(source);
            requests.Add(request);
        }

        if (parents.Count(p => p == null) != 1)
            throw new InvalidDataException(id + ": topology must have exactly one root.");
        var linkedItems = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < ids.Count; i++)
        {
            if ((parents[i] == null) != (containers[i] == null))
                throw new InvalidDataException(id + "/" + ids[i] + ": parent and container references must both be null or present.");
            if (parents[i] == null) continue;
            int parent = ids.IndexOf(parents[i]!);
            if (parent < 0 || parent == i)
                throw new InvalidDataException(id + "/" + ids[i] + ": invalid parent grid reference.");
            if (!fixedContainers[parent].Contains(containers[i]!))
                throw new InvalidDataException(id + "/" + ids[i] + ": parent container is not a fixed obstacle.");
            if (!linkedItems.Add(containers[i]!))
                throw new InvalidDataException(id + ": container item backs multiple grids: " + containers[i]);
            var ancestors = new HashSet<string>(StringComparer.Ordinal) { ids[i] };
            string? ancestor = parents[i];
            while (ancestor != null)
            {
                int ancestorIndex = ids.IndexOf(ancestor);
                if (ancestorIndex < 0)
                    throw new InvalidDataException(id + ": unknown ancestor grid " + ancestor);
                if (!ancestors.Add(ancestor))
                    throw new InvalidDataException(id + ": cyclic grid topology at " + ancestor);
                ancestor = parents[ancestorIndex];
            }
        }
        if (linkedItems.Count != fixedContainers.Sum(ids => ids.Count))
            throw new InvalidDataException(id + ": fixed container has no child grid.");
        return new Case(id, family, ids.ToArray(), sources.ToArray(), requests.ToArray());
    }

    private static void ValidateRequestShape(JObject request)
    {
        Integer(request, "Width");
        Integer(request, "Height");
        foreach (JToken token in Array(request, "Fixed"))
        {
            JObject block = Object(token, "fixed block");
            Integer(block, "X"); Integer(block, "Y");
            Integer(block, "Width"); Integer(block, "Height");
        }
        foreach (JToken token in Array(request, "Items"))
        {
            JObject item = Object(token, "item");
            Text(item, "Id"); Text(item, "TemplateId");
            Integer(item, "Width"); Integer(item, "Height");
            Required(item, "Required", JTokenType.Boolean);
            Required(item, "SortType", JTokenType.String);
            foreach (JToken part in Array(item, "SubcategoryPath"))
                NonemptyString(part, "SubcategoryPath entry");
        }
        foreach (JToken token in Array(request, "Current"))
        {
            JObject placement = Object(token, "current placement");
            Text(placement, "Id"); Integer(placement, "X"); Integer(placement, "Y");
            Required(placement, "Rotated", JTokenType.Boolean);
        }
        foreach (JToken category in Array(request, "CategoryOrder"))
            if (category.Type != JTokenType.String)
                throw new InvalidDataException("CategoryOrder entries must be type keys (including the empty unclassified key).");
    }

    private static JObject Report(Case sample, PackRequest[] requests,
        ContainerPackResult result, double elapsedMs)
    {
        var errors = new List<string>();
        var grids = new JArray();
        var initial = new ContainerPackResult(requests.Select(request =>
            new PackResult(request.Current, System.Array.Empty<PackItem>())).ToArray());
        BigInteger inputScore = CategoryOrder.Score(requests, initial);
        bool initialOrderLegal = requests.Select((request, i) =>
            CategoryOrder.Penalty(request, initial.Grids[i])).All(penalty => penalty == 0);
        bool canScore = result.Grids.Count == requests.Length;
        if (!canScore) errors.Add("grid count changed: expected " + requests.Length + ", got " + result.Grids.Count);
        for (int i = 0; i < result.Grids.Count; i++)
        {
            PackResult layout = result.Grids[i];
            var metrics = new JObject
            {
                ["Id"] = i < sample.GridIds.Length ? sample.GridIds[i] : null,
                ["Height"] = null, ["Spans"] = null, ["Rows"] = null,
                ["Columns"] = null, ["Score"] = null, ["OrderPenalty"] = null,
                ["Placements"] = JArray.FromObject(layout.Placements),
                ["Unplaced"] = JArray.FromObject(layout.Unplaced),
                ["Warning"] = layout.Warning, ["Diagnostic"] = layout.Diagnostic,
            };
            grids.Add(metrics);
            if (i >= requests.Length) continue;
            var gridErrors = new List<string>();
            ValidateLayout(requests[i], layout, gridErrors);
            if (gridErrors.Count == 0)
            {
                long penalty = CategoryOrder.Penalty(requests[i], layout);
                metrics["Height"] = CpSatPacker.Height(requests[i], layout);
                metrics["Spans"] = JArray.FromObject(CategoryPacking.Spans(requests[i], layout));
                metrics["Rows"] = CategoryPacking.CoordinateSum(requests[i], layout, false);
                metrics["Columns"] = CategoryPacking.CoordinateSum(requests[i], layout, true);
                metrics["Score"] = CategoryPacking.Score(requests[i], layout).ToString(CultureInfo.InvariantCulture);
                metrics["OrderPenalty"] = penalty;
                if (penalty != 0) gridErrors.Add("category order penalty " + penalty);
            }
            else canScore = false;
            errors.AddRange(gridErrors.Select(error => sample.GridIds[i] + ": " + error));
        }
        BigInteger? score = canScore ? CategoryOrder.Score(requests, result) : (BigInteger?)null;
        if (initialOrderLegal && score.HasValue && score.Value > inputScore)
            errors.Add("full score regressed from legal input layout");
        return new JObject
        {
            ["CaseId"] = sample.Id, ["Family"] = sample.Family,
            ["ElapsedMs"] = elapsedMs,
            ["Score"] = score?.ToString(CultureInfo.InvariantCulture),
            ["InputScore"] = inputScore.ToString(CultureInfo.InvariantCulture),
            ["InputOrderLegal"] = initialOrderLegal,
            ["Valid"] = errors.Count == 0, ["Errors"] = JArray.FromObject(errors),
            ["Warning"] = result.Warning, ["Diagnostic"] = result.Diagnostic,
            ["Grids"] = grids,
        };
    }

    private static void ValidateLayout(PackRequest request, PackResult result, List<string> errors)
    {
        if (!result.Complete)
            errors.Add("unplaced items: " + string.Join(", ", result.Unplaced.Select(item => item.Id)));
        var items = request.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var rectangles = new List<(long X, long Y, long Width, long Height, string Name)>();
        for (int i = 0; i < request.Fixed.Count; i++)
        {
            FixedBlock block = request.Fixed[i];
            Mark(block.X, block.Y, block.Width, block.Height, "obstacle " + i);
        }
        foreach (Placement placement in result.Placements)
        {
            if (placement == null || string.IsNullOrWhiteSpace(placement.Id))
            {
                errors.Add("null placement or missing placement ID");
                continue;
            }
            if (!placed.Add(placement.Id)) errors.Add("duplicate placement " + placement.Id);
            if (!items.TryGetValue(placement.Id, out PackItem? item))
            {
                errors.Add("unknown or foreign-grid item " + placement.Id);
                continue;
            }
            Mark(placement.X, placement.Y, placement.Rotated ? item.Height : item.Width,
                placement.Rotated ? item.Width : item.Height, "item " + placement.Id);
        }
        foreach (string itemId in items.Keys)
            if (!placed.Contains(itemId)) errors.Add("missing item " + itemId);

        void Mark(long x, long y, long width, long height, string name)
        {
            if (width <= 0 || height <= 0 || x < 0 || y < 0
                || x + width > request.Width || y + height > request.Height)
                errors.Add(name + " is out of bounds or has nonpositive dimensions");
            foreach (var rectangle in rectangles)
                if (x < rectangle.X + rectangle.Width && x + width > rectangle.X
                    && y < rectangle.Y + rectangle.Height && y + height > rectangle.Y)
                    errors.Add(name + " overlaps " + rectangle.Name);
            rectangles.Add((x, y, width, height, name));
        }
    }

    private static JObject Object(JToken? value, string label) => value as JObject
        ?? throw new InvalidDataException(label + " must be an object.");

    private static JToken Required(JObject value, string key, JTokenType type)
    {
        JToken? token = value[key];
        if (token == null || token.Type != type)
            throw new InvalidDataException(key + " must be present and have type " + type);
        return token;
    }

    private static JArray Array(JObject value, string key) => (JArray)Required(value, key, JTokenType.Array);
    private static int Integer(JObject value, string key) => Required(value, key, JTokenType.Integer).Value<int>();
    private static string Text(JObject value, string key) => NonemptyString(Required(value, key, JTokenType.String), key);

    private static string NonemptyString(JToken value, string label)
    {
        if (value.Type != JTokenType.String || string.IsNullOrWhiteSpace(value.Value<string>()))
            throw new InvalidDataException(label + " must be a nonempty string.");
        return value.Value<string>()!;
    }

    private static string? NullableText(JObject value, string key)
    {
        JToken? token = value[key];
        if (token == null) throw new InvalidDataException("Missing " + key);
        return token.Type == JTokenType.Null ? null : NonemptyString(token, key);
    }

    private sealed class Case
    {
        internal readonly string Id;
        internal readonly string Family;
        internal readonly string[] GridIds;
        internal readonly PackRequest[] Requests;
        private readonly JObject[] _sources;

        internal Case(string id, string family, string[] gridIds, JObject[] sources, PackRequest[] requests)
        {
            Id = id;
            Family = family;
            GridIds = gridIds;
            _sources = sources;
            Requests = requests;
        }

        internal PackRequest[] FreshRequests() => _sources.Select(source => source.ToObject<PackRequest>()!).ToArray();
    }
}
