using System.Collections.Frozen;
using Sw5e.Domain.Content;

namespace Sw5e.Infrastructure.Content;

/// <summary>
/// The immutable, fully built in-memory index the filesystem store answers
/// from. Constructed once at startup and never mutated, so it needs no locking.
/// </summary>
internal sealed class ContentIndex
{
    private readonly FrozenDictionary<string, TypeIndex> _byType;

    private ContentIndex(FrozenDictionary<string, TypeIndex> byType, string version)
    {
        _byType = byType;
        Version = version;
    }

    /// <summary>
    /// Token covering the whole index, mixed into every response version so a
    /// content reload invalidates caches wholesale.
    /// </summary>
    public string Version { get; }

    /// <summary>An index holding nothing, which is what an absent content directory produces.</summary>
    public static ContentIndex Empty { get; } = Create([], "empty");

    public static ContentIndex Create(
        IEnumerable<IndexedContentItem> items,
        string version)
    {
        var grouped = items
            .GroupBy(item => item.Type.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new TypeIndex(group),
                StringComparer.Ordinal);

        foreach (var definition in ContentTypeRegistry.All)
        {
            // Every registered type gets an entry, empty or not, so the
            // registry endpoint reports a count of zero rather than omitting a
            // type the site still needs a navigation entry for.
            grouped.TryAdd(definition.Key, new TypeIndex([]));
        }

        return new ContentIndex(grouped.ToFrozenDictionary(StringComparer.Ordinal), version);
    }

    public int Count(ContentTypeDefinition type) => For(type).Items.Count;

    /// <summary>Items of one type, ordered by key. Never null: an unpopulated type yields an empty list.</summary>
    public IReadOnlyList<IndexedContentItem> Items(ContentTypeDefinition type) => For(type).Items;

    public IndexedContentItem? Find(ContentTypeDefinition type, string key) =>
        For(type).ByKey.TryGetValue(key, out var item) ? item : null;

    private TypeIndex For(ContentTypeDefinition type) =>
        _byType.TryGetValue(type.Key, out var index) ? index : TypeIndex.Empty;

    private sealed class TypeIndex
    {
        public static TypeIndex Empty { get; } = new([]);

        public TypeIndex(IEnumerable<IndexedContentItem> items)
        {
            Items = items.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
            ByKey = Items.ToFrozenDictionary(item => item.Key, StringComparer.Ordinal);
        }

        public IReadOnlyList<IndexedContentItem> Items { get; }

        public FrozenDictionary<string, IndexedContentItem> ByKey { get; }
    }
}
