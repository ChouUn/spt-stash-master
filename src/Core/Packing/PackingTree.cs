using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ChouUn.StashMaster.Core.Packing;

/// <summary>每个请求的排序与聚合共用树：类型为根，原生子类和末端模板按成员裁剪并压缩。</summary>
internal sealed class PackingTree
{
    private static readonly ConditionalWeakTable<PackRequest, PackingTree> Cache = new();

    internal sealed class Node
    {
        internal Node(int index, string key, int depth, string sortType,
            string? categoryId, string? templateId, Node? parent, IReadOnlyList<PackItem> items)
        {
            Index = index;
            Key = key;
            Depth = depth;
            SortType = sortType;
            CategoryId = categoryId;
            TemplateId = templateId;
            Parent = parent;
            Items = items;
        }

        internal int Index { get; }
        internal string Key { get; }
        internal int Depth { get; }
        internal string SortType { get; }
        internal string? CategoryId { get; }
        internal string? TemplateId { get; }
        internal Node? Parent { get; }
        internal IReadOnlyList<PackItem> Items { get; }
    }

    private sealed class Branch
    {
        internal Branch(string? categoryId, string? templateId = null)
        {
            CategoryId = categoryId;
            TemplateId = templateId;
        }
        internal string? CategoryId { get; }
        internal string? TemplateId { get; }
        internal List<PackItem> Items { get; } = new();
        internal Dictionary<(bool Template, string Id), Branch> Children { get; } = new();

        internal Branch Child(string id, bool template = false)
        {
            var key = (Template: template, Id: id);
            if (!Children.TryGetValue(key, out Branch? child))
            {
                child = template ? new Branch(null, id) : new Branch(id);
                Children.Add(key, child);
            }
            return child;
        }
    }

    internal static PackingTree For(PackRequest request) =>
        Cache.GetValue(request, static key => new PackingTree(key));

    internal IReadOnlyList<Node> Nodes { get; }
    internal IReadOnlyList<Node> Roots { get; }
    internal IReadOnlyList<IReadOnlyList<Node>> Levels { get; }
    internal IReadOnlyDictionary<string, IReadOnlyList<Node>> Paths { get; }

    private PackingTree(PackRequest request)
    {
        var ranks = request.CategoryOrder.Select((type, rank) => (type, rank))
            .ToDictionary(p => p.type, p => p.rank, StringComparer.Ordinal);
        var nodes = new List<Node>();
        var roots = new List<Node>();
        var levels = new List<List<Node>>();
        var paths = request.Items.ToDictionary(i => i.Id, _ => new List<Node>());
        foreach (var type in request.Items.GroupBy(i => i.SortType, StringComparer.Ordinal)
            .OrderBy(g => ranks.TryGetValue(g.Key, out int rank) ? rank : int.MaxValue)
            .ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var root = new Branch(null);
            foreach (PackItem item in type)
            {
                Branch branch = root;
                branch.Items.Add(item);
                // 适配层已按业务类型截断；只构建类型内部的子链，不再猜测原生边界。
                for (int depth = 0; depth < item.SubcategoryPath.Count; depth++)
                {
                    branch = branch.Child(item.SubcategoryPath[depth]);
                    branch.Items.Add(item);
                }
                branch.Child(item.TemplateId, template: true).Items.Add(item);
            }
            Add(root, null, type.Key);
        }
        Nodes = nodes.ToArray();
        Roots = roots.ToArray();
        Levels = levels.Select(level => (IReadOnlyList<Node>)level.ToArray()).ToArray();
        Paths = paths.ToDictionary(p => p.Key, p => (IReadOnlyList<Node>)p.Value.ToArray());

        void Add(Branch branch, Node? parent, string sortType)
        {
            // 同成员子节点不增加区分能力。存在直属成员时，子节点是严格子集，仍保留。
            Node? node = parent;
            if (parent == null || branch.Items.Count != parent.Items.Count)
            {
                int depth = parent == null ? 0 : parent.Depth + 1;
                string key = parent == null ? "type:" + Encode(sortType)
                    : parent.Key + "/" + (branch.TemplateId == null
                        ? "category:" + Encode(branch.CategoryId!)
                        : "template:" + Encode(branch.TemplateId));
                node = new Node(nodes.Count, key, depth, sortType,
                    branch.CategoryId, branch.TemplateId, parent, branch.Items.ToArray());
                nodes.Add(node);
                if (parent == null) { roots.Add(node); }
                if (levels.Count == depth) { levels.Add(new List<Node>()); }
                levels[depth].Add(node);
                foreach (PackItem item in branch.Items) { paths[item.Id].Add(node); }
            }
            foreach (Branch child in branch.Children.OrderBy(p => p.Key.Template)
                .ThenBy(p => p.Key.Id, StringComparer.Ordinal).Select(p => p.Value))
                Add(child, node, sortType);
        }
    }

    internal static (int First, int End) Measure(Node node,
        IReadOnlyDictionary<string, Placement> positions)
    {
        int first = int.MaxValue;
        int end = 0;
        foreach (PackItem item in node.Items)
        {
            if (!positions.TryGetValue(item.Id, out Placement? p)) { continue; }
            first = Math.Min(first, p.Y);
            end = Math.Max(end, p.Y + (p.Rotated ? item.Width : item.Height));
        }
        return end == 0 ? (0, 0) : (first, end);
    }

    private static string Encode(string value) => value.Length + ":" + value;
}
