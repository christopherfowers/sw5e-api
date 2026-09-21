namespace Sw5e.Domain.Content;

/// <summary>
/// Static description of one content type. Everything here except
/// <see cref="ItemCount"/> is a compile-time constant, which is what makes the
/// registry safe to validate route input against: a request can only ever
/// select one of these entries, never supply a name of its own.
/// </summary>
/// <param name="Key">Canonical type key, used as the <c>{type}</c> route value.</param>
/// <param name="DisplayName">Singular label for a heading or a breadcrumb.</param>
/// <param name="PluralName">Plural label for a navigation entry.</param>
/// <param name="RouteSegment">Slug the site uses in its own URLs.</param>
/// <param name="ItemCount">How many items of this type the store currently holds.</param>
public sealed record ContentTypeDescriptor(
    string Key,
    string DisplayName,
    string PluralName,
    string RouteSegment,
    int ItemCount);
