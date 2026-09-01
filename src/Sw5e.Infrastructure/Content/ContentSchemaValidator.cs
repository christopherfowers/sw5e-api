using System.Text.Json;
using System.Text.Json.Nodes;
using Sw5e.Database.Schemas;
using Sw5e.Domain.Content;

namespace Sw5e.Infrastructure.Content;

/// <summary>
/// Validates authored documents using the content repository's own schema
/// validator.
/// </summary>
/// <remarks>
/// <para>
/// This type holds no validation logic. Everything it does is resolve which
/// schema version applies and hand the document to
/// <see cref="SchemaValidator"/> — the same class, from the same assembly, that
/// the content repository's CI runs over the whole corpus on every pull
/// request. That is the entire reason the schema project is referenced through
/// a submodule rather than reimplemented here: two validators drift, and the
/// symptom of drift is a document that the API accepted and CI later rejects,
/// discovered only once it is already in the corpus.
/// </para>
/// <para>
/// The schema documents are read from disk. They ship in the API image
/// alongside the application, copied out of the same submodule the validator
/// comes from, so the code and the schemas it evaluates are always from one
/// commit of one repository.
/// </para>
/// </remarks>
public sealed class ContentSchemaValidator : IContentSchemaValidator
{
    private readonly SchemaRepository _repository;
    private readonly SchemaValidator _validator;

    /// <summary>
    /// The version assumed for a type whose schema directory cannot be read.
    /// </summary>
    /// <remarks>
    /// Every schema in the repository is at v1 today. This constant is what a
    /// probe falls back to, not a hard-coded answer: the probe reads the
    /// directory, so publishing <c>v2.json</c> is picked up without a code
    /// change, which is the property the design asks for — a content type's
    /// definition is a reviewed schema file, never a migration.
    /// </remarks>
    public const int FallbackVersion = SchemaRepository.FallbackVersion;

    public ContentSchemaValidator(string schemaRootPath)
        // Throws when the directory is absent. Deliberately not softened: an
        // API that starts without its schemas would accept every write and
        // validate none of them, and would look completely healthy doing it.
        : this(new SchemaRepository(
            !string.IsNullOrWhiteSpace(schemaRootPath)
                ? schemaRootPath
                : throw new ArgumentException(
                    "A schema root path is required.", nameof(schemaRootPath))))
    {
    }

    /// <summary>
    /// Uses a repository somebody else built.
    /// </summary>
    /// <remarks>
    /// The exporter needs the same schemas this validator holds, and each
    /// <see cref="SchemaRepository"/> compiles and caches all 31 of them
    /// separately. Sharing one is not only cheaper, it is the difference
    /// between a deployment where the validator and the exporter could be
    /// looking at two different directories and one where they cannot.
    /// </remarks>
    public ContentSchemaValidator(SchemaRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _validator = new SchemaValidator(_repository);
    }

    /// <inheritdoc />
    public int CurrentVersion(ContentTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return _repository.LatestVersion(type.Key);
    }

    /// <inheritdoc />
    public ContentValidation Validate(ContentTypeDefinition type, int version, JsonElement body)
    {
        ArgumentNullException.ThrowIfNull(type);

        // JsonNode rather than JsonElement because that is what the shared
        // validator takes. The round trip through the raw text is the
        // documented way across, and it is over a document that has already
        // been parsed once, so nothing is being re-validated as JSON here.
        JsonNode? node;

        try
        {
            node = JsonNode.Parse(body.GetRawText());
        }
        catch (JsonException exception)
        {
            return ContentValidation.Invalid([exception.Message]);
        }

        if (node is null)
        {
            return ContentValidation.Invalid(["The document is null."]);
        }

        try
        {
            var result = _validator.Validate(type.Key, version, node);

            return result.IsValid
                ? ContentValidation.Valid
                : ContentValidation.Invalid(result.Errors);
        }
        catch (SchemaNotFoundException)
        {
            // A registered content type with no schema on disk. Refused rather
            // than waved through: "there is no schema for this type" must never
            // be the same outcome as "the document conforms", or a packaging
            // mistake silently disables validation for one type.
            return ContentValidation.Invalid(
                [$"No schema is published for content type '{type.Key}' at version {version}."]);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Delegated to the repository's own reader rather than opening the file
    /// again here. That reader already answers the document as it is written —
    /// key order and all — and caches it, and going through it is what keeps
    /// the schema that is served and the schema that is evaluated provably the
    /// same file rather than two files that happen to agree.
    /// </para>
    /// <para>
    /// Null rather than the exception, because a registered type with no schema
    /// file is not an error on this path. It is a packaging fact, and a client
    /// that generates an editor from these has something sensible to do about
    /// it: fall back to editing the document directly. The write path still
    /// refuses documents of that type loudly, so nothing is waved through by
    /// answering null here.
    /// </para>
    /// </remarks>
    public JsonElement? Published(ContentTypeDefinition type, int version)
    {
        ArgumentNullException.ThrowIfNull(type);

        try
        {
            return _repository.GetDocument(type.Key, version);
        }
        catch (SchemaNotFoundException)
        {
            return null;
        }
    }
}
