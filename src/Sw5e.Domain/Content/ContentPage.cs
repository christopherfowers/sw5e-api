namespace Sw5e.Domain.Content;

/// <summary>
/// One document and everything needed to render its page.
/// </summary>
/// <remarks>
/// <para>
/// A page on the site is not one document. A class page is the class plus
/// every feature that class grants, plus its archetypes and its improvements,
/// and working that out means reading two and a half thousand feature
/// documents. That join used to happen once, at build time, on a machine
/// holding the whole catalogue, which is why a change published through the
/// CMS never reached a reader: the assembled page only existed as a build
/// output, and nothing rebuilt it.
/// </para>
/// <para>
/// So the join moves here, where the data already is. The reference table is
/// written on every import and holds all of it: two thousand six hundred and
/// eighty-two <c>grantedBy</c> edges, a hundred and sixty-seven <c>class</c>
/// edges. Nothing has to be computed, only returned.
/// </para>
/// <para>
/// <see cref="Related"/> is one hop, in both directions. One hop is what the
/// renderer actually asks for: the features a class grants point at the class,
/// and a feature points back at the class that grants it. Two hops would pull
/// an archetype's whole feature list onto a class page that only names the
/// archetype, which is a larger response for something nobody draws.
/// </para>
/// </remarks>
/// <param name="Item">The document the page is about.</param>
/// <param name="Related">
/// Documents one edge away, grouped by content type. A type with no related
/// documents is absent rather than present and empty, so a renderer reading
/// this cannot tell "nothing is related" apart from "this type does not relate",
/// which it never needs to.
/// </param>
/// <param name="Version">
/// A token covering the item and everything related to it.
///
/// It has to cover the whole bundle rather than just the item. A feature being
/// republished changes the class page that lists it while leaving the class
/// document untouched, and a validator built from the item alone would serve
/// that reader a cached page that is now wrong.
/// </param>
public sealed record ContentPage(
    ContentDocument Item,
    IReadOnlyDictionary<string, IReadOnlyList<ContentDocument>> Related,
    string Version);
